using System.Collections.ObjectModel;
using PrinterMate.App.Mvvm;
using PrinterMate.App.Services;
using PrinterMate.Catalog;
using PrinterMate.Core;
using PrinterMate.Core.Drivers;

namespace PrinterMate.App.ViewModels;

/// <summary>内置驱动库中的一条驱动(展平到品牌分组下)。</summary>
public sealed class DriverEntryRow
{
    public DriverEntryRow(DriverCatalogEntry entry, DriverAvailability availability)
    {
        Brand = entry.Brand;
        Family = entry.Family;
        DriverName = entry.DriverName;
        PdlText = entry.Pdl switch
        {
            PrintDescriptionLanguage.ClassDriver => "Class Driver",
            PrintDescriptionLanguage.PCL6 => "PCL6",
            PrintDescriptionLanguage.PS => "PostScript",
            PrintDescriptionLanguage.GDI => "GDI",
            _ => "未知",
        };

        ArchText = entry.Arch.Count == 0 ? "任意" : string.Join(" / ", entry.Arch);
        RedistributableText = entry.Redistributable ? "已确认" : "未确认";
        IsAvailable = availability.Available;
        AvailableText = availability.Available ? "已入库" : "不可用";
        Reason = availability.Reason;
        IsInbox = entry.IsInboxDriver;
    }

    public string Brand { get; }

    public string Family { get; }

    public string DriverName { get; }

    public string PdlText { get; }

    public string ArchText { get; }

    public string RedistributableText { get; }

    public bool IsAvailable { get; }

    public string AvailableText { get; }

    public string Reason { get; }

    public bool IsInbox { get; }
}

/// <summary>按品牌分组的驱动条目。</summary>
public sealed class DriverBrandGroup
{
    public DriverBrandGroup(string brand, IReadOnlyList<DriverEntryRow> entries)
    {
        Brand = brand;
        Entries = entries;
    }

    public string Brand { get; }

    public string Header => $"{Brand}({Entries.Count} 条)";

    public IReadOnlyList<DriverEntryRow> Entries { get; }
}

/// <summary>品牌维度的可用性统计。</summary>
public sealed class BrandSummaryRow
{
    public BrandSummaryRow(string brand, int total, int ready)
    {
        Brand = brand;
        Total = total;
        Ready = ready;
    }

    public string Brand { get; }

    public int Total { get; }

    public int Ready { get; }

    public int Unavailable => Total - Ready;

    public string SummaryText => $"共 {Total} 条,可用 {Ready} 条,不可用 {Unavailable} 条";
}

/// <summary>品牌维度:本机是否允许使用该品牌的内置驱动。</summary>
public sealed class BrandPolicyRow : ObservableObject
{
    private readonly Action<string, bool> _onChanged;
    private bool _isBuiltinAllowed;

    public BrandPolicyRow(string brand, string summaryText, bool isBuiltinAllowed, Action<string, bool> onChanged)
    {
        Brand = brand;
        SummaryText = summaryText;
        _isBuiltinAllowed = isBuiltinAllowed;
        _onChanged = onChanged;
    }

    public string Brand { get; }

    public string SummaryText { get; }

    public bool IsBuiltinAllowed
    {
        get => _isBuiltinAllowed;
        set
        {
            if (SetProperty(ref _isBuiltinAllowed, value))
            {
                _onChanged(Brand, value);
            }
        }
    }

    /// <summary>只更新界面状态,不回调(用于把复选框还原成真实策略,避免递归弹窗)。</summary>
    public void SetSilently(bool value)
    {
        if (_isBuiltinAllowed == value)
        {
            return;
        }

        _isBuiltinAllowed = value;
        OnPropertyChanged(nameof(IsBuiltinAllowed));
    }
}

/// <summary>驱动管理页。</summary>
public sealed class DriverManagementViewModel : ObservableObject, IActivatable
{
    private const string AllBrands = "全部品牌";

    /// <summary>开启内置厂商驱动时记录的声明文本,会写入本机策略文件与审计日志。</summary>
    private const string DisclosureText = "使用者声明:本机个人使用,不对外分发本软件与驱动包。";

    private readonly AppServices _services;
    private readonly ThirdPartyDriverSiteLauncher _siteLauncher = new();

    private bool _isBusy;
    private bool _suppressPolicyCallback;
    private bool _allowAllBuiltin;
    private string _siteModel = string.Empty;
    private DriverSiteSearch? _selectedSite;

    private string _keyword = string.Empty;
    private string _brandFilter = AllBrands;
    private DriverCandidate? _selectedManualCandidate;
    private string _manualSummary = "尚未检索。请选择品牌或输入型号关键词后点击「检索候选驱动」。";
    private string _manualSelectionText = "当前连接时将使用自动匹配结果。";

    public DriverManagementViewModel(AppServices services)
    {
        _services = services;

        SearchCandidatesCommand = new AsyncRelayCommand(() => SearchCandidatesAsync(), () => !IsBusy);
        UseCandidateCommand = new RelayCommand(UseCandidate, () => SelectedManualCandidate is { Installable: true });
        RefreshCommand = new RelayCommand(LoadLibrary);
        OpenSiteSearchCommand = new RelayCommand(OpenSiteSearch);

        foreach (var site in ThirdPartyDriverSiteLauncher.Sites)
        {
            DriverSites.Add(site);
        }

        _selectedSite = DriverSites.FirstOrDefault();

        LoadLibrary();
    }

    /// <summary>已注册的第三方驱动站检索入口。</summary>
    public ObservableCollection<DriverSiteSearch> DriverSites { get; } = new();

    public RelayCommand OpenSiteSearchCommand { get; }

    /// <summary>第三方站检索用的型号输入。</summary>
    public string SiteModel
    {
        get => _siteModel;
        set => SetProperty(ref _siteModel, value);
    }

    public DriverSiteSearch? SelectedSite
    {
        get => _selectedSite;
        set => SetProperty(ref _selectedSite, value);
    }

    public string SiteHint =>
        "程序只负责在浏览器中打开搜索页,不会下载、不会安装、也不会放宽任何签名校验。"
        + "这些站点并非厂商官方来源,下载与安装的风险由你自己承担。";

    public ObservableCollection<DriverBrandGroup> BrandGroups { get; } = new();

    public ObservableCollection<BrandSummaryRow> BrandSummaries { get; } = new();

    public ObservableCollection<BrandPolicyRow> BrandPolicies { get; } = new();

    public string PolicyHint =>
        "清单里的「可再分发」是发行方的红线,默认关闭。若你只在本机自己使用、不把本软件和驱动包发给别人,"
        + "可以在这里显式开启某品牌的内置驱动。开启只影响「是否允许从本地驱动库安装」，"
        + "驱动包仍然必须通过 SHA256 与 .cat 数字签名校验,校验不会因此放宽。";

    public string PolicySummary { get; private set; } = "-";

    public bool HasBrandPolicies => BrandPolicies.Count > 0;

    /// <summary>全局开关:开启后所有品牌的内置驱动都可在本机安装。</summary>
    public bool AllowAllBuiltinDrivers
    {
        get => _allowAllBuiltin;
        set
        {
            if (!SetProperty(ref _allowAllBuiltin, value) || _suppressPolicyCallback)
            {
                return;
            }

            ApplyGlobalPolicy(value);
        }
    }

    public ObservableCollection<string> MissingPackages { get; } = new();

    public ObservableCollection<DriverCandidate> ManualCandidates { get; } = new();

    public ObservableCollection<string> Brands { get; } = new();

    public AsyncRelayCommand SearchCandidatesCommand { get; }

    public RelayCommand UseCandidateCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public bool HasLibrary => _services.Library is not null;

    public string Root { get; private set; } = "-";

    public string ManifestPath { get; private set; } = "-";

    public string LibraryVersion { get; private set; } = "-";

    public string ManifestSource { get; private set; } = "-";

    public string ArchitectureText { get; private set; } = "-";

    public int TotalEntries { get; private set; }

    public int InboxEntries { get; private set; }

    public int PackagedEntries { get; private set; }

    public int ReadyEntries { get; private set; }

    public int UnavailableEntries => TotalEntries - ReadyEntries;

    public string LibraryHeadline => HasLibrary
        ? $"内置驱动库共 {TotalEntries} 条:系统免驱 {InboxEntries} 条,本地驱动包 {PackagedEntries} 条,当前可直接使用 {ReadyEntries} 条。"
        : "内置驱动库不可用,无法进行驱动匹配。";

    public bool HasMissingPackages => MissingPackages.Count > 0;

    public string MissingHeader => $"以下 {MissingPackages.Count} 条驱动当前不可直接安装(需要补齐驱动包或改用在线源):";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SearchCandidatesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Keyword
    {
        get => _keyword;
        set => SetProperty(ref _keyword, value);
    }

    public string BrandFilter
    {
        get => _brandFilter;
        set => SetProperty(ref _brandFilter, value);
    }

    public DriverCandidate? SelectedManualCandidate
    {
        get => _selectedManualCandidate;
        set
        {
            if (SetProperty(ref _selectedManualCandidate, value))
            {
                UseCandidateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ManualSummary
    {
        get => _manualSummary;
        private set => SetProperty(ref _manualSummary, value);
    }

    public string ManualSelectionText
    {
        get => _manualSelectionText;
        private set => SetProperty(ref _manualSelectionText, value);
    }

    public void OnActivated() => LoadLibrary();

    public void OnDeactivated()
    {
    }

    private void LoadLibrary()
    {
        LoadPolicyRows();
        RefreshAvailability();
    }

    /// <summary>刷新品牌维度的内置驱动许可开关。</summary>
    private void LoadPolicyRows()
    {
        BrandPolicies.Clear();

        var library = _services.Library;
        if (library is null)
        {
            PolicySummary = "内置驱动库不可用,无法调整内置驱动策略。";
            _suppressPolicyCallback = true;
            AllowAllBuiltinDrivers = false;
            _suppressPolicyCallback = false;
            OnPropertyChanged(nameof(PolicySummary));
            OnPropertyChanged(nameof(HasBrandPolicies));
            return;
        }

        foreach (var brand in library.Brands)
        {
            var entries = library.ForBrand(brand);
            var summary = entries.All(e => e.IsInboxDriver)
                ? "系统内置类驱动,无需授权开关"
                : $"共 {entries.Count} 条,清单标记可再分发 {entries.Count(e => e.Redistributable)} 条";

            BrandPolicies.Add(new BrandPolicyRow(brand, summary, library.Policy.IsEnabled(brand), ToggleBrandPolicy));
        }

        PolicySummary = library.Policy.Describe();
        _suppressPolicyCallback = true;
        AllowAllBuiltinDrivers = library.Policy.AllowsAll;
        _suppressPolicyCallback = false;

        OnPropertyChanged(nameof(PolicySummary));
        OnPropertyChanged(nameof(HasBrandPolicies));
    }

    private void ApplyGlobalPolicy(bool enabled)
    {
        var library = _services.Library;
        if (library is null)
        {
            RevertPolicyToggle(enabled);
            return;
        }

        if (!ConfirmPolicyChange(enabled, "全部品牌"))
        {
            RevertPolicyToggle(enabled);
            return;
        }

        library.Policy.SetAll(enabled, enabled ? DisclosureText : string.Empty);
        ReportPolicyChange(enabled, "全部品牌");
        LoadLibrary();
    }

    private void ToggleBrandPolicy(string brand, bool enabled)
    {
        var library = _services.Library;
        if (library is null)
        {
            return;
        }

        if (!ConfirmPolicyChange(enabled, brand))
        {
            SyncPolicyRowsFromStore();
            return;
        }

        library.SetBrandDistribution(brand, enabled, enabled ? DisclosureText : string.Empty);
        ReportPolicyChange(enabled, brand);
        SyncPolicyRowsFromStore();
        RefreshAvailability();
    }

    /// <summary>用户取消时把复选框状态还原(静默,避免再次弹确认框)。</summary>
    private void RevertPolicyToggle(bool attempted)
    {
        _suppressPolicyCallback = true;
        AllowAllBuiltinDrivers = !attempted;
        _suppressPolicyCallback = false;
    }

    private void SyncPolicyRowsFromStore()
    {
        var library = _services.Library;
        if (library is null)
        {
            return;
        }

        foreach (var row in BrandPolicies)
        {
            row.SetSilently(library.Policy.IsEnabled(row.Brand));
        }

        PolicySummary = library.Policy.Describe();
        OnPropertyChanged(nameof(PolicySummary));
    }

    private bool ConfirmPolicyChange(bool enabling, string scope) => _services.Dialogs.Confirm(
        enabling ? "确认开启内置驱动" : "确认关闭内置驱动",
        enabling
            ? $"即将为「{scope}」开启内置驱动安装许可。\n\n{PolicyHint}\n\n{DisclosureText}\n\n是否继续?"
            : $"即将为「{scope}」关闭内置驱动安装许可。关闭后该品牌只能改用厂商官方在线源。\n\n是否继续?",
        enabling ? "我确认,开启" : "确认关闭",
        "取消");

    private void ReportPolicyChange(bool enabled, string scope)
    {
        var path = _services.Library?.Policy.FilePath ?? "-";
        AppLog.Write("distribution-policy", $"{scope} -> {enabled}; {DisclosureText}; 策略文件={path}");

        if (enabled)
        {
            StatusHub.Success(
                "内置驱动许可",
                $"已为「{scope}」开启内置驱动安装许可。",
                "若该品牌驱动包已入库,现在可以完全离线安装;签名与哈希校验仍然强制执行。");
        }
        else
        {
            StatusHub.Warn(
                "内置驱动许可",
                $"已为「{scope}」关闭内置驱动安装许可。",
                "该品牌将改用厂商官方在线源下载驱动。");
        }
    }

    private void RefreshAvailability()
    {
        BrandGroups.Clear();
        BrandSummaries.Clear();
        MissingPackages.Clear();
        Brands.Clear();

        var library = _services.Library;
        if (library is null)
        {
            ArchitectureText = DriverMatchEngine.CurrentArchitecture;
            RaiseAll();
            return;
        }

        Brands.Add(AllBrands);
        foreach (var brand in library.Brands)
        {
            Brands.Add(brand);
        }

        if (!Brands.Contains(_brandFilter))
        {
            BrandFilter = AllBrands;
        }

        var status = library.GetStatus(DriverMatchEngine.CurrentArchitecture);
        Root = status.Root;
        ManifestPath = status.ManifestPath;
        LibraryVersion = string.IsNullOrWhiteSpace(status.LibraryVersion) ? "(未标注)" : status.LibraryVersion;
        ManifestSource = status.UsingEmbeddedManifest ? "程序内置资源(未找到磁盘清单)" : "磁盘清单文件";
        ArchitectureText = $"{DriverMatchEngine.CurrentArchitecture} / {DriverMatchEngine.CurrentOsTag}";
        TotalEntries = status.TotalEntries;
        InboxEntries = status.InboxEntries;
        PackagedEntries = status.PackagedEntries;
        ReadyEntries = status.ReadyEntries;

        foreach (var brand in library.Brands)
        {
            var entries = library.ForBrand(brand);
            var rows = entries
                .Select(entry => new DriverEntryRow(entry, library.CheckAvailability(entry, DriverMatchEngine.CurrentArchitecture, DriverMatchEngine.CurrentOsTag)))
                .ToList();

            BrandGroups.Add(new DriverBrandGroup(brand, rows));
            BrandSummaries.Add(new BrandSummaryRow(brand, rows.Count, rows.Count(r => r.IsAvailable)));
        }

        foreach (var missing in status.MissingPackages)
        {
            MissingPackages.Add(missing);
        }

        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(HasLibrary));
        OnPropertyChanged(nameof(Root));
        OnPropertyChanged(nameof(ManifestPath));
        OnPropertyChanged(nameof(LibraryVersion));
        OnPropertyChanged(nameof(ManifestSource));
        OnPropertyChanged(nameof(ArchitectureText));
        OnPropertyChanged(nameof(TotalEntries));
        OnPropertyChanged(nameof(InboxEntries));
        OnPropertyChanged(nameof(PackagedEntries));
        OnPropertyChanged(nameof(ReadyEntries));
        OnPropertyChanged(nameof(UnavailableEntries));
        OnPropertyChanged(nameof(LibraryHeadline));
        OnPropertyChanged(nameof(HasMissingPackages));
        OnPropertyChanged(nameof(MissingHeader));
        OnPropertyChanged(nameof(Brands));
        OnPropertyChanged(nameof(PolicySummary));
        OnPropertyChanged(nameof(HasBrandPolicies));
        OnPropertyChanged(nameof(AllowAllBuiltinDrivers));
    }

    private async Task SearchCandidatesAsync()
    {
        if (_services.MatchEngine is null)
        {
            StatusHub.Error("人工选择驱动", "驱动匹配引擎不可用。", string.Join(" ", _services.StartupErrors));
            return;
        }

        IsBusy = true;
        StatusHub.Info("人工选择驱动", "正在检索内置驱动库 …");
        try
        {
            var brand = string.Equals(BrandFilter, AllBrands, StringComparison.Ordinal) ? null : BrandFilter;
            var keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();

            var candidates = await Task.Run(() => _services.MatchEngine!.ListManualCandidates(brand, keyword));

            ManualCandidates.Clear();
            foreach (var candidate in candidates)
            {
                ManualCandidates.Add(candidate);
            }

            var usable = candidates.Count(c => c.Installable);
            ManualSummary = candidates.Count == 0
                ? "未找到匹配的驱动,请更换品牌或关键词。"
                : $"共 {candidates.Count} 个候选驱动,其中 {usable} 个可用。选中一条后点击「设为连接时使用的驱动」。";

            if (candidates.Count == 0)
            {
                StatusHub.Warn("人工选择驱动", "未找到匹配的驱动。", "请更换品牌或型号关键词;若该品牌驱动包尚未入库,请使用厂商官方安装包。");
            }
            else
            {
                StatusHub.Success("人工选择驱动", $"共检索到 {candidates.Count} 个候选驱动(可用 {usable} 个)。");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("manual-search", ex.ToString());
            StatusHub.Error("人工选择驱动", "检索失败:" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenSiteSearch()
    {
        var site = SelectedSite;
        if (site is null)
        {
            StatusHub.Warn("第三方驱动站", "请先选择要打开的站点。");
            return;
        }

        var prepared = _siteLauncher.Prepare(site.Id, SiteModel);
        if (!prepared.IsSuccess || prepared.Value is null)
        {
            StatusHub.Warn("第三方驱动站", prepared.Message, prepared.Suggestion ?? string.Empty);
            return;
        }

        // 二次确认里承载完整的风险告知,不依赖用户去别处阅读。
        if (!_services.Dialogs.Confirm(
                "风险自担:打开第三方驱动站",
                ThirdPartyDriverSiteLauncher.MandatoryWarning + "\n\n" + prepared.Value.Description,
                "我已知晓,打开",
                "取消"))
        {
            StatusHub.Info("第三方驱动站", "已取消打开。");
            return;
        }

        var opened = _siteLauncher.Open(prepared.Value);
        if (opened.IsSuccess)
        {
            StatusHub.Warn("第三方驱动站", opened.Message, opened.Suggestion ?? string.Empty);
        }
        else
        {
            StatusHub.Error("第三方驱动站", opened.Message, opened.Suggestion ?? string.Empty);
        }
    }

    private void UseCandidate()
    {
        var candidate = SelectedManualCandidate;
        if (candidate is null)
        {
            return;
        }

        if (!candidate.Installable)
        {
            StatusHub.Warn(
                "人工选择驱动",
                $"该驱动当前不可用:{candidate.BlockReason}",
                "请选择标记为「可用」的驱动,或先补齐该品牌的驱动包。");
            return;
        }

        if (!_services.Dialogs.Confirm(
                "确认使用该驱动",
                $"将把「{candidate.DriverName}」({candidate.PdlText} / {candidate.SourceText})指定为后续连接使用的驱动。\n\n之后在「设备发现与连接」页点击「一键连接」时会优先使用它,直到你清除人工选择。\n\n是否继续?",
                "确定使用",
                "取消"))
        {
            return;
        }

        _services.ManualCandidate = candidate;
        ManualSelectionText = $"已指定人工驱动:{candidate.DriverName}({candidate.SourceText},{candidate.PdlText})";
        StatusHub.Success(
            "人工选择驱动",
            $"已指定「{candidate.DriverName}」为连接时使用的驱动。",
            "请到「设备发现与连接」页点击目标设备的「一键连接」。");
    }
}

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

/// <summary>驱动管理页。</summary>
public sealed class DriverManagementViewModel : ObservableObject, IActivatable
{
    private const string AllBrands = "全部品牌";

    private readonly AppServices _services;

    private bool _isBusy;
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

        LoadLibrary();
    }

    public ObservableCollection<DriverBrandGroup> BrandGroups { get; } = new();

    public ObservableCollection<BrandSummaryRow> BrandSummaries { get; } = new();

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

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PrinterMate.App.Mvvm;
using PrinterMate.App.Services;
using PrinterMate.Core.Drivers;
using PrinterMate.Core.Elevation;
using PrinterMate.Core.Infrastructure;
using PrinterMate.Interop;
using PrinterMate.Printing;
using PrinterMate.Security;

namespace PrinterMate.App.ViewModels;

/// <summary>白名单厂商域名行。</summary>
public sealed class VendorDomainRow
{
    public VendorDomainRow(VendorDomain domain)
    {
        Brand = domain.Brand;
        Domain = domain.Domain;
        Note = string.IsNullOrWhiteSpace(domain.Note) ? string.Empty : domain.Note;
    }

    public string Brand { get; }

    public string Domain { get; }

    public string Note { get; }

    public string DisplayText => string.IsNullOrWhiteSpace(Note)
        ? $"{Brand}  —  {Domain}"
        : $"{Brand}  —  {Domain}({Note})";
}

/// <summary>设置页:默认打印机、WPP、日志目录、白名单、环境信息。</summary>
public sealed class SettingsViewModel : ObservableObject, IActivatable
{
    private readonly AppServices _services;

    private bool _isBusy;
    private string _defaultPrinter = "(未设置)";
    private QueuePrinterInfo? _selectedPrinter;
    private bool _wppEnabled;
    private string _wppSource = "-";
    private string _wppDetail = "-";
    private string _auditDirectory = "-";
    private string _whitelistError = string.Empty;
    private string _environmentSummary = string.Empty;

    public SettingsViewModel(AppServices services)
    {
        _services = services;

        RefreshCommand = new RelayCommand(Load);
        SetDefaultPrinterCommand = new AsyncRelayCommand(SetDefaultPrinterAsync, () => !IsBusy && _selectedPrinter is not null);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        OpenPrinterSettingsCommand = new RelayCommand(OpenPrinterSettings);

        Load();
    }

    public ObservableCollection<QueuePrinterInfo> Printers { get; } = new();

    public ObservableCollection<string> MicrosoftDomains { get; } = new();

    public ObservableCollection<VendorDomainRow> VendorDomains { get; } = new();

    public RelayCommand RefreshCommand { get; }

    public AsyncRelayCommand SetDefaultPrinterCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand OpenPrinterSettingsCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SetDefaultPrinterCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DefaultPrinter
    {
        get => _defaultPrinter;
        private set => SetProperty(ref _defaultPrinter, value);
    }

    public QueuePrinterInfo? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (SetProperty(ref _selectedPrinter, value))
            {
                SetDefaultPrinterCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool WppEnabled
    {
        get => _wppEnabled;
        private set
        {
            if (SetProperty(ref _wppEnabled, value))
            {
                OnPropertyChanged(nameof(WppStateText));
            }
        }
    }

    public string WppStateText => _wppEnabled
        ? "已启用(仅允许系统内置类驱动,第三方打印驱动安装会被拒绝)"
        : "未启用(可正常安装内置驱动)";

    public string WppSource
    {
        get => _wppSource;
        private set => SetProperty(ref _wppSource, value);
    }

    public string WppDetail
    {
        get => _wppDetail;
        private set => SetProperty(ref _wppDetail, value);
    }

    public string AuditDirectory
    {
        get => _auditDirectory;
        private set => SetProperty(ref _auditDirectory, value);
    }

    public string WhitelistError
    {
        get => _whitelistError;
        private set
        {
            if (SetProperty(ref _whitelistError, value))
            {
                OnPropertyChanged(nameof(HasWhitelistError));
            }
        }
    }

    public bool HasWhitelistError => !string.IsNullOrWhiteSpace(_whitelistError);

    public string EnvironmentSummary
    {
        get => _environmentSummary;
        private set => SetProperty(ref _environmentSummary, value);
    }

    public string SystemVersion => Environment.OSVersion.VersionString;

    public string SystemBuild => Environment.OSVersion.Version.Build.ToString();

    public string ArchitectureText { get; private set; } = "-";

    public bool PowerShellAvailable => PowerShellRunner.IsAvailable;

    public string PowerShellText => PowerShellRunner.IsAvailable
        ? "可用:" + PowerShellRunner.ExecutablePath
        : "不可用(未找到 pwsh 或 powershell.exe)";

    public bool HelperAvailable => ElevatedLauncher.IsHelperAvailable;

    public string HelperText => ElevatedLauncher.IsHelperAvailable
        ? "已就绪:" + ElevatedLauncher.HelperPath
        : "缺失:" + ElevatedLauncher.HelperPath + "(驱动安装可能因权限不足失败)";

    public bool IsElevated => ElevatedLauncher.IsProcessElevated();

    public string ElevationText => ElevatedLauncher.IsProcessElevated()
        ? "当前进程以管理员权限运行"
        : "当前进程以普通用户权限运行(安装驱动时会弹出 UAC 提权确认)";

    public string RuntimeText => RuntimeInformation.FrameworkDescription;

    public void OnActivated() => Load();

    public void OnDeactivated()
    {
    }

    private void Load()
    {
        try
        {
            DefaultPrinter = SpoolerApi.GetDefaultPrinter() ?? "(未设置)";
        }
        catch (Exception ex)
        {
            AppLog.Write("settings-default", ex.ToString());
            DefaultPrinter = "读取失败:" + ex.Message;
        }

        try
        {
            var previous = _selectedPrinter?.Name;
            var printers = _services.Queue.GetLocalPrinters();
            Printers.Clear();
            foreach (var printer in printers)
            {
                Printers.Add(printer);
            }

            SelectedPrinter = Printers.FirstOrDefault(p => string.Equals(p.Name, previous, StringComparison.OrdinalIgnoreCase))
                ?? Printers.FirstOrDefault(p => string.Equals(p.Name, DefaultPrinter, StringComparison.OrdinalIgnoreCase))
                ?? Printers.FirstOrDefault();
        }
        catch (Exception ex)
        {
            AppLog.Write("settings-printers", ex.ToString());
        }

        try
        {
            var wpp = _services.Wpp.Detect();
            WppEnabled = wpp.Enabled;
            WppSource = string.IsNullOrWhiteSpace(wpp.Source) ? "-" : wpp.Source;
            WppDetail = string.IsNullOrWhiteSpace(wpp.Detail) ? "-" : wpp.Detail;
        }
        catch (Exception ex)
        {
            AppLog.Write("settings-wpp", ex.ToString());
            WppSource = "检测异常";
            WppDetail = ex.Message;
        }

        AuditDirectory = _services.Audit?.Directory ?? "(审计日志不可用)";

        LoadWhitelist();
        LoadEnvironment();
        RaiseDerived();
    }

    private void LoadWhitelist()
    {
        MicrosoftDomains.Clear();
        VendorDomains.Clear();
        WhitelistError = string.Empty;

        try
        {
            var allowlist = DownloadSourceAllowlist.Default;
            foreach (var domain in allowlist.MicrosoftDomains)
            {
                MicrosoftDomains.Add(domain);
            }

            foreach (var vendor in allowlist.VendorDomains)
            {
                VendorDomains.Add(new VendorDomainRow(vendor));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("settings-whitelist", ex.ToString());
            WhitelistError = "下载源白名单加载失败:" + ex.Message;
        }
    }

    private void LoadEnvironment()
    {
        var architecture = RuntimeInformation.OSArchitecture.ToString();
        ArchitectureText = $"{DriverMatchEngine.CurrentArchitecture} / 系统 {architecture} / {DriverMatchEngine.CurrentOsTag}";
        EnvironmentSummary = $"系统版本 {Environment.OSVersion.VersionString}(内部版本 {Environment.OSVersion.Version.Build}) · " +
                             $"运行库 {RuntimeInformation.FrameworkDescription}";
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(SystemVersion));
        OnPropertyChanged(nameof(SystemBuild));
        OnPropertyChanged(nameof(ArchitectureText));
        OnPropertyChanged(nameof(PowerShellAvailable));
        OnPropertyChanged(nameof(PowerShellText));
        OnPropertyChanged(nameof(HelperAvailable));
        OnPropertyChanged(nameof(HelperText));
        OnPropertyChanged(nameof(IsElevated));
        OnPropertyChanged(nameof(ElevationText));
        OnPropertyChanged(nameof(RuntimeText));
        OnPropertyChanged(nameof(EnvironmentSummary));
        OnPropertyChanged(nameof(WppStateText));
    }

    private async Task SetDefaultPrinterAsync()
    {
        var printer = _selectedPrinter;
        if (printer is null)
        {
            return;
        }

        if (!_services.Dialogs.Confirm(
                "设为默认打印机",
                $"即将把「{printer.Name}」设置为系统默认打印机。\n\n是否继续?",
                "设为默认",
                "取消"))
        {
            StatusHub.Warn("设为默认打印机", "已取消。");
            return;
        }

        IsBusy = true;
        StatusHub.Info("设为默认打印机", $"正在把「{printer.Name}」设置为默认打印机 …");
        try
        {
            var name = printer.Name;
            var error = await Task.Run(() => SetDefaultPrinterCore(name));
            if (error is null)
            {
                DefaultPrinter = name;
                StatusHub.Success("设为默认打印机", $"已将「{name}」设置为系统默认打印机。");
            }
            else
            {
                StatusHub.Error("设为默认打印机", "设置失败:" + error, "请在「控制面板 → 设备和打印机」中手动设置默认打印机。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string? SetDefaultPrinterCore(string printerName)
    {
        try
        {
            var rundll32 = Path.Combine(Environment.SystemDirectory, "rundll32.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = File.Exists(rundll32) ? rundll32 : "rundll32.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            startInfo.ArgumentList.Add("printui.dll,PrintUIEntry");
            startInfo.ArgumentList.Add("/y");
            startInfo.ArgumentList.Add("/n");
            startInfo.ArgumentList.Add(printerName);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "无法启动 printui.dll,PrintUIEntry。";
            }

            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 已自行退出
                }

                return "设置默认打印机超时(30 秒)。";
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var detail = stderr.GetAwaiter().GetResult();
                return string.IsNullOrWhiteSpace(detail)
                    ? $"printui 返回退出码 {process.ExitCode}。"
                    : detail.Trim();
            }

            return null;
        }
        catch (Exception ex)
        {
            AppLog.Write("settings-setdefault", ex.ToString());
            return ex.Message;
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            var directory = _services.Audit?.Directory ?? AppLog.Directory;
            System.IO.Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            StatusHub.Success("打开日志目录", "已打开:" + directory);
        }
        catch (Exception ex)
        {
            AppLog.Write("settings-openlog", ex.ToString());
            StatusHub.Error("打开日志目录", "打开失败:" + ex.Message, "请手动复制路径后在资源管理器中打开。");
        }
    }

    private void OpenPrinterSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "control", Arguments = "printers", UseShellExecute = true });
            StatusHub.Success("打开打印机管理", "已打开系统「设备和打印机」窗口。");
        }
        catch (Exception ex)
        {
            AppLog.Write("settings-opencontrol", ex.ToString());
            StatusHub.Error("打开打印机管理", "打开失败:" + ex.Message, "请在开始菜单搜索「打印机和扫描仪」手动打开。");
        }
    }
}

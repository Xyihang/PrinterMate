using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using PrinterMate.App.Mvvm;
using PrinterMate.App.Services;
using PrinterMate.Core.Discovery;
using PrinterMate.Printing;

namespace PrinterMate.App.ViewModels;

/// <summary>打印与测试页。所有打印操作都在后台线程执行,UI 不阻塞。</summary>
public sealed class PrintingViewModel : ObservableObject, IActivatable
{
    private const string SupportedFilter =
        "支持的文件|*.txt;*.log;*.csv;*.md;*.json;*.xml;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.xps;*.oxps;*.pdf;*.prn;*.ps;*.pcl;*.zpl;*.bin|" +
        "文本文件|*.txt;*.log;*.csv;*.md;*.json;*.xml|" +
        "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|" +
        "RAW/指令文件|*.prn;*.ps;*.pcl;*.zpl;*.bin|" +
        "所有文件|*.*";

    private readonly AppServices _services;

    private QueuePrinterInfo? _selectedPrinter;
    private bool _isBusy;
    private string _selectedFilePath = "(未选择文件)";
    private string _detectedKindText = "尚未选择文件。";
    private string _printerSummary = "尚未加载打印机列表。";

    public PrintingViewModel(AppServices services)
    {
        _services = services;

        RefreshPrintersCommand = new AsyncRelayCommand(() => LoadPrintersAsync(), () => !IsBusy);
        PrintSystemTestPageCommand = new AsyncRelayCommand(PrintSystemTestPageAsync, CanPrint);
        PrintCustomTestPageCommand = new AsyncRelayCommand(PrintCustomTestPageAsync, CanPrint);
        PrintFileCommand = new AsyncRelayCommand(PrintFileAsync, CanPrint);
        PrintRawCommand = new AsyncRelayCommand(PrintRawAsync, CanPrint);
    }

    public ObservableCollection<QueuePrinterInfo> Printers { get; } = new();

    public AsyncRelayCommand RefreshPrintersCommand { get; }

    public AsyncRelayCommand PrintSystemTestPageCommand { get; }

    public AsyncRelayCommand PrintCustomTestPageCommand { get; }

    public AsyncRelayCommand PrintFileCommand { get; }

    public AsyncRelayCommand PrintRawCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshPrintersCommand.RaiseCanExecuteChanged();
                PrintSystemTestPageCommand.RaiseCanExecuteChanged();
                PrintCustomTestPageCommand.RaiseCanExecuteChanged();
                PrintFileCommand.RaiseCanExecuteChanged();
                PrintRawCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public QueuePrinterInfo? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (SetProperty(ref _selectedPrinter, value))
            {
                PrintSystemTestPageCommand.RaiseCanExecuteChanged();
                PrintCustomTestPageCommand.RaiseCanExecuteChanged();
                PrintFileCommand.RaiseCanExecuteChanged();
                PrintRawCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SelectedPrinterText));
            }
        }
    }

    public string SelectedPrinterText => _selectedPrinter is null
        ? "未选择打印机。"
        : $"当前打印机:{_selectedPrinter.Name}  ·  驱动:{_selectedPrinter.DriverName}  ·  端口:{_selectedPrinter.PortName}  ·  {( _selectedPrinter.IsNetwork ? "网络" : "本地")}";

    public string PrinterSummary
    {
        get => _printerSummary;
        private set => SetProperty(ref _printerSummary, value);
    }

    public string SelectedFilePath
    {
        get => _selectedFilePath;
        private set => SetProperty(ref _selectedFilePath, value);
    }

    public string DetectedKindText
    {
        get => _detectedKindText;
        private set => SetProperty(ref _detectedKindText, value);
    }

    public void OnActivated() => _ = LoadPrintersAsync();

    public void OnDeactivated()
    {
    }

    private bool CanPrint() => !IsBusy && _selectedPrinter is not null;

    private async Task LoadPrintersAsync()
    {
        IsBusy = true;
        try
        {
            var printers = await Task.Run(() => _services.Queue.GetLocalPrinters());
            var previous = _selectedPrinter?.Name;

            Printers.Clear();
            foreach (var printer in printers)
            {
                Printers.Add(printer);
            }

            SelectedPrinter = Printers.FirstOrDefault(p => string.Equals(p.Name, previous, StringComparison.OrdinalIgnoreCase))
                ?? Printers.FirstOrDefault();

            PrinterSummary = Printers.Count == 0
                ? "本机没有可用的打印队列。请先在「设备发现与连接」页连接打印机。"
                : $"本机共有 {Printers.Count} 个打印队列。";

            if (Printers.Count == 0)
            {
                StatusHub.Warn("加载打印机", "本机没有可用的打印队列。", "请先在「设备发现与连接」页通过 USB 或局域网连接打印机。");
            }
            else
            {
                StatusHub.Success("加载打印机", $"已加载 {Printers.Count} 个打印队列。");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("printers", ex.ToString());
            StatusHub.Error("加载打印机", "加载失败:" + ex.Message, "请确认后台打印服务(Print Spooler)正在运行。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PrintSystemTestPageAsync()
    {
        var printer = _selectedPrinter;
        if (printer is null)
        {
            return;
        }

        IsBusy = true;
        StatusHub.Info("打印系统测试页", $"正在向「{printer.Name}」提交 Windows 系统测试页 …");
        try
        {
            var result = await Task.Run(() => TryRun(() => SystemTestPage.Print(printer.Name)));
            Publish("打印系统测试页", result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PrintCustomTestPageAsync()
    {
        var printer = _selectedPrinter;
        if (printer is null)
        {
            return;
        }

        IsBusy = true;
        StatusHub.Info("打印自绘测试页", $"正在向「{printer.Name}」提交自绘矢量测试页 …");
        try
        {
            var model = new TestPageModel(
                printer.Name,
                DeviceNameParser.ExtractAddress(printer.PortName) ?? "-",
                string.IsNullOrWhiteSpace(printer.DriverName) ? "-" : printer.DriverName,
                string.IsNullOrWhiteSpace(printer.PortName) ? "-" : printer.PortName,
                InstallHelper.GuessPdl(printer.DriverName),
                "彩色 + 灰阶(由驱动决定)",
                "A4 纵向",
                DateTimeOffset.Now,
                new List<KeyValuePair<string, string>>
                {
                    new("连接类型", printer.IsNetwork ? "网络" : "本地"),
                    new("共享状态", printer.IsShared ? "已共享" : "未共享"),
                    new("队列作业数", printer.JobCount.ToString()),
                    new("生成程序", "PrinterMate"),
                });

            var result = await Task.Run(() => TryRun(() => TestPageRenderer.Print(printer.Name, model)));
            Publish("打印自绘测试页", result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PrintFileAsync()
    {
        var printer = _selectedPrinter;
        if (printer is null)
        {
            return;
        }

        var path = PickFile();
        if (path is null)
        {
            return;
        }

        var kind = FilePrintService.DetectKind(path);
        SelectedFilePath = path;
        DetectedKindText = $"文件类型判定:{DescribeKind(kind)}";

        IsBusy = true;
        StatusHub.Info("打印本地文件", $"正在通过「{printer.Name}」打印 {Path.GetFileName(path)}({DescribeKind(kind)})…");
        try
        {
            var result = await Task.Run(() => TryRun(() => FilePrintService.Print(printer.Name, path)));
            Publish("打印本地文件", result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PrintRawAsync()
    {
        var printer = _selectedPrinter;
        if (printer is null)
        {
            return;
        }

        var path = PickFile();
        if (path is null)
        {
            return;
        }

        SelectedFilePath = path;
        DetectedKindText = $"文件类型判定:{DescribeKind(FilePrintService.DetectKind(path))}(RAW 投递将原样发送字节)";

        var confirmed = _services.Dialogs.Confirm(
            "投递 RAW 数据",
            $"即将把文件「{Path.GetFileName(path)}」的全部字节以 RAW 数据类型直接发送到打印队列「{printer.Name}」。\n\n" +
            "RAW 投递不会做任何渲染或转换:如果文件不是打印机原生指令(ZPL/PCL/PostScript/PRN),打印机可能打印出乱码或完全无输出。\n\n是否继续?",
            "投递",
            "取消");

        if (!confirmed)
        {
            StatusHub.Warn("投递 RAW 数据", "已取消。");
            return;
        }

        IsBusy = true;
        StatusHub.Info("投递 RAW 数据", $"正在向「{printer.Name}」投递 {Path.GetFileName(path)} …");
        try
        {
            var result = await Task.Run(() => TryRun(() => FilePrintService.PrintRaw(printer.Name, path)));
            Publish("投递 RAW 数据", result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string? PickFile()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择要打印的文件",
                Filter = SupportedFilter,
                CheckFileExists = true,
                Multiselect = false,
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
        catch (Exception ex)
        {
            AppLog.Write("file-dialog", ex.ToString());
            return null;
        }
    }

    /// <summary>把同步打印调用收敛成 OperationResult,中文异常提示。</summary>
    private static (bool Ok, string Message, string Suggestion) TryRun(Action action)
    {
        try
        {
            action();
            return (true, "已提交打印任务,请查看打印机出纸情况。", string.Empty);
        }
        catch (TimeoutException ex)
        {
            return (false, ex.Message, "请确认打印机已开机、未暂停,并在打印队列中查看是否有卡住的作业。");
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, ex.Message, "请以管理员身份重新打开本程序后重试。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message, "请检查打印机连接与队列状态,或导出诊断报告反馈。");
        }
    }

    private static void Publish(string operation, (bool Ok, string Message, string Suggestion) result)
        => StatusHub.Report(
            operation,
            result.Message,
            result.Ok ? StatusLevel.Success : StatusLevel.Error,
            result.Suggestion);

    private static string DescribeKind(FilePrintKind kind) => kind switch
    {
        FilePrintKind.Text => "文本(自绘排版后经 XPS 通道打印)",
        FilePrintKind.Image => "图片(等比缩放后经 XPS 通道打印)",
        FilePrintKind.Xps => "XPS 文档(原样投递)",
        FilePrintKind.Pdf => "PDF(调用系统默认程序打印)",
        FilePrintKind.Raw => "RAW/打印机指令(原样投递)",
        _ => "不支持的类型",
    };
}

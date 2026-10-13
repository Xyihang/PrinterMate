using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using PrinterMate.App.Mvvm;
using PrinterMate.App.Services;
using PrinterMate.Printing;

namespace PrinterMate.App.ViewModels;

/// <summary>打印作业列表中的一行。</summary>
public sealed class QueueJobRow
{
    public QueueJobRow(QueueJobInfo job)
    {
        Job = job;
        Document = string.IsNullOrWhiteSpace(job.Document) ? "(未命名文档)" : job.Document;
        UserName = string.IsNullOrWhiteSpace(job.UserName) ? "-" : job.UserName;
        PageText = job.TotalPages > 0 ? $"{job.PagesPrinted}/{job.TotalPages} 页" : "-";
        SubmittedText = job.Submitted is null ? "-" : job.Submitted.Value.ToString("yyyy-MM-dd HH:mm:ss");
        StatusText = DescribeJob(job);
    }

    public QueueJobInfo Job { get; }

    public string Document { get; }

    public string UserName { get; }

    public string PageText { get; }

    public string SubmittedText { get; }

    public string StatusText { get; }

    private static string DescribeJob(QueueJobInfo job)
    {
        if (!string.IsNullOrWhiteSpace(job.StatusText))
        {
            return job.StatusText;
        }

        var health = PrinterStatusMapper.MapJobHealth(job.Status);
        return PrinterStatusMapper.Describe(health);
    }
}

/// <summary>任务队列与状态页(5 秒自动刷新,页面不可见时停止)。</summary>
public sealed class QueueViewModel : ObservableObject, IActivatable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;

    private QueuePrinterInfo? _selectedPrinter;
    private QueueJobRow? _selectedJob;
    private bool _isBusy;
    private bool _refreshing;
    private string _healthText = "未选择打印机。";
    private string _healthAdvice = "请先在上方选择一台打印机。";
    private StatusLevel _healthLevel = StatusLevel.Info;
    private string _jobsSummary = "尚未加载作业列表。";
    private bool _autoRefreshEnabled = true;

    public QueueViewModel(AppServices services)
    {
        _services = services;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) =>
        {
            if (_autoRefreshEnabled && !IsBusy)
            {
                _ = RefreshAsync(silent: true);
            }
        };

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(silent: false), () => !IsBusy);
        PauseQueueCommand = new AsyncRelayCommand(() => QueueActionAsync("暂停队列", () => _services.Queue.PauseQueue(RequirePrinter())), () => CanUseQueue);
        ResumeQueueCommand = new AsyncRelayCommand(() => QueueActionAsync("恢复队列", () => _services.Queue.ResumeQueue(RequirePrinter())), () => CanUseQueue);
        PurgeQueueCommand = new AsyncRelayCommand(PurgeAsync, () => CanUseQueue);
        PauseJobCommand = new AsyncRelayCommand(() => JobActionAsync("暂停作业", () => RequireJob()), () => CanUseJob);
        ResumeJobCommand = new AsyncRelayCommand(() => JobActionAsync("恢复作业", () => RequireJob()), () => CanUseJob);
        CancelJobCommand = new AsyncRelayCommand(() => JobActionAsync("取消作业", () => RequireJob()), () => CanUseJob);
        ExportDiagnosticsCommand = new AsyncRelayCommand(ExportDiagnosticsAsync, () => !IsBusy);
    }

    public ObservableCollection<QueuePrinterInfo> Printers { get; } = new();

    public ObservableCollection<QueueJobRow> Jobs { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand PauseQueueCommand { get; }

    public AsyncRelayCommand ResumeQueueCommand { get; }

    public AsyncRelayCommand PurgeQueueCommand { get; }

    public AsyncRelayCommand PauseJobCommand { get; }

    public AsyncRelayCommand ResumeJobCommand { get; }

    public AsyncRelayCommand CancelJobCommand { get; }

    public AsyncRelayCommand ExportDiagnosticsCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseAllCommands();
            }
        }
    }

    public bool AutoRefreshEnabled
    {
        get => _autoRefreshEnabled;
        set => SetProperty(ref _autoRefreshEnabled, value);
    }

    public QueuePrinterInfo? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (SetProperty(ref _selectedPrinter, value))
            {
                OnPropertyChanged(nameof(SelectedPrinterText));
                RaiseAllCommands();
                _ = LoadJobsAsync();
            }
        }
    }

    public QueueJobRow? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (SetProperty(ref _selectedJob, value))
            {
                RaiseAllCommands();
            }
        }
    }

    public string SelectedPrinterText => _selectedPrinter is null
        ? "未选择打印机。"
        : $"当前队列:{_selectedPrinter.Name}  ·  驱动:{_selectedPrinter.DriverName}  ·  端口:{_selectedPrinter.PortName}";

    public string HealthText
    {
        get => _healthText;
        private set => SetProperty(ref _healthText, value);
    }

    public string HealthAdvice
    {
        get => _healthAdvice;
        private set => SetProperty(ref _healthAdvice, value);
    }

    public StatusLevel HealthLevel
    {
        get => _healthLevel;
        private set => SetProperty(ref _healthLevel, value);
    }

    public string JobsSummary
    {
        get => _jobsSummary;
        private set => SetProperty(ref _jobsSummary, value);
    }

    private bool CanUseQueue => !IsBusy && _selectedPrinter is not null;

    private bool CanUseJob => CanUseQueue && _selectedJob is not null;

    public void OnActivated()
    {
        _timer.Start();
        _ = RefreshAsync(silent: false);
    }

    public void OnDeactivated() => _timer.Stop();

    private void RaiseAllCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        PauseQueueCommand.RaiseCanExecuteChanged();
        ResumeQueueCommand.RaiseCanExecuteChanged();
        PurgeQueueCommand.RaiseCanExecuteChanged();
        PauseJobCommand.RaiseCanExecuteChanged();
        ResumeJobCommand.RaiseCanExecuteChanged();
        CancelJobCommand.RaiseCanExecuteChanged();
        ExportDiagnosticsCommand.RaiseCanExecuteChanged();
    }

    private string RequirePrinter() => _selectedPrinter?.Name ?? throw new InvalidOperationException("尚未选择打印机。");

    private int RequireJob() => _selectedJob?.Job.JobId ?? throw new InvalidOperationException("尚未选择打印作业。");

    private async Task RefreshAsync(bool silent)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        if (!silent)
        {
            IsBusy = true;
            StatusHub.Info("刷新队列", "正在读取本机打印队列与作业 …");
        }

        try
        {
            var printers = await Task.Run(() => _services.Queue.GetLocalPrinters());
            var previous = _selectedPrinter?.Name;

            if (!Printers.SequenceEqual(printers))
            {
                Printers.Clear();
                foreach (var printer in printers)
                {
                    Printers.Add(printer);
                }

                SelectedPrinter = Printers.FirstOrDefault(p => string.Equals(p.Name, previous, StringComparison.OrdinalIgnoreCase))
                    ?? Printers.FirstOrDefault();
            }

            await LoadJobsAsync();

            if (!silent)
            {
                if (Printers.Count == 0)
                {
                    StatusHub.Warn("刷新队列", "本机没有可用的打印队列。", "请先在「设备发现与连接」页连接打印机。");
                }
                else
                {
                    StatusHub.Success("刷新队列", $"已刷新「{SelectedPrinter?.Name}」,当前 {Jobs.Count} 个作业。");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("queue-refresh", ex.ToString());
            if (!silent)
            {
                StatusHub.Error("刷新队列", "刷新失败:" + ex.Message, "请确认后台打印服务(Print Spooler)正在运行。");
            }
        }
        finally
        {
            if (!silent)
            {
                IsBusy = false;
            }

            _refreshing = false;
        }
    }

    private async Task LoadJobsAsync()
    {
        var printer = _selectedPrinter;
        if (printer is null)
        {
            Jobs.Clear();
            JobsSummary = "未选择打印机。";
            HealthText = "未选择打印机。";
            HealthAdvice = "请先在上方选择一台打印机。";
            HealthLevel = StatusLevel.Info;
            OnPropertyChanged(nameof(Jobs));
            return;
        }

        try
        {
            var selectedId = _selectedJob?.Job.JobId;
            var jobs = await Task.Run(() => _services.Queue.GetJobs(printer.Name));

            Jobs.Clear();
            foreach (var job in jobs)
            {
                Jobs.Add(new QueueJobRow(job));
            }

            SelectedJob = Jobs.FirstOrDefault(j => j.Job.JobId == selectedId);

            var health = await Task.Run(() => _services.Queue.GetHealth(printer.Name));
            HealthText = $"{PrinterStatusMapper.Describe(health)}({printer.Name})";
            HealthAdvice = PrinterStatusMapper.Suggest(health);
            HealthLevel = LevelOf(health);

            JobsSummary = Jobs.Count == 0
                ? $"「{printer.Name}」当前没有排队中的作业。"
                : $"「{printer.Name}」共有 {Jobs.Count} 个作业(队列自身作业数 {printer.JobCount})。";
        }
        catch (Exception ex)
        {
            AppLog.Write("queue-jobs", ex.ToString());
            JobsSummary = "读取作业失败:" + ex.Message;
            HealthText = "状态读取失败";
            HealthAdvice = "请确认后台打印服务(Print Spooler)正在运行,然后点击「刷新」。";
            HealthLevel = StatusLevel.Error;
        }
    }

    private async Task QueueActionAsync(string operation, Action action)
    {
        IsBusy = true;
        try
        {
            await Task.Run(action);
            StatusHub.Success(operation, $"已对队列「{_selectedPrinter?.Name}」执行「{operation}」。");
            await RefreshAsync(silent: true);
        }
        catch (Exception ex)
        {
            AppLog.Write("queue-action", ex.ToString());
            StatusHub.Error(operation, $"{operation}失败:{ex.Message}", "请确认当前账户有权限操作该打印队列,必要时以管理员身份运行。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PurgeAsync()
    {
        var printer = _selectedPrinter;
        if (printer is null)
        {
            return;
        }

        if (!_services.Dialogs.Confirm(
                "清空打印队列",
                $"即将删除「{printer.Name}」上所有未完成的打印作业(共 {printer.JobCount} 个)。\n\n被清空的作业无法恢复,需要重新提交。是否继续?",
                "清空队列",
                "取消"))
        {
            StatusHub.Warn("清空队列", "已取消。");
            return;
        }

        IsBusy = true;
        try
        {
            var name = printer.Name;
            await Task.Run(() => _services.Queue.PurgeQueue(name));
            StatusHub.Success("清空队列", $"已清空「{name}」的未完成作业。");
            await RefreshAsync(silent: true);
        }
        catch (Exception ex)
        {
            AppLog.Write("queue-purge", ex.ToString());
            StatusHub.Error("清空队列", "清空失败:" + ex.Message, "请确认当前账户有权限管理该打印队列。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task JobActionAsync(string operation, Func<int> jobSelector)
    {
        IsBusy = true;
        try
        {
            var printerName = RequirePrinter();
            var jobId = jobSelector();
            await Task.Run(() =>
            {
                switch (operation)
                {
                    case "暂停作业":
                        _services.Queue.PauseJob(printerName, jobId);
                        break;
                    case "恢复作业":
                        _services.Queue.ResumeJob(printerName, jobId);
                        break;
                    default:
                        _services.Queue.CancelJob(printerName, jobId);
                        break;
                }
            });

            StatusHub.Success(operation, $"已对作业 #{jobId} 执行「{operation}」。");
            await RefreshAsync(silent: true);
        }
        catch (Exception ex)
        {
            AppLog.Write("job-action", ex.ToString());
            StatusHub.Error(operation, $"{operation}失败:{ex.Message}", "请确认当前账户有权限管理打印作业。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportDiagnosticsAsync()
    {
        string? path = null;
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出诊断报告",
                Filter = "JSON 文件|*.json|文本文件|*.txt|所有文件|*.*",
                FileName = $"PrinterMate-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                OverwritePrompt = true,
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            path = dialog.FileName;
        }
        catch (Exception ex)
        {
            AppLog.Write("diagnostics-dialog", ex.ToString());
            StatusHub.Error("导出诊断报告", "无法打开保存对话框:" + ex.Message);
            return;
        }

        IsBusy = true;
        StatusHub.Info("导出诊断报告", "正在收集设备、驱动库、队列与审计日志 …");
        try
        {
            await Task.Run(() => DiagnosticsExporter.WriteToFile(path, _services));
            StatusHub.Success(
                "导出诊断报告",
                $"诊断报告已导出到:{path}",
                "报告包含设备列表、驱动库状态、队列与作业、最近审计日志,可直接反馈给维护人员。");
        }
        catch (Exception ex)
        {
            AppLog.Write("diagnostics", ex.ToString());
            StatusHub.Error("导出诊断报告", "导出失败:" + ex.Message, "请更换保存位置(例如桌面)后重试。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static StatusLevel LevelOf(PrinterHealth health) => health switch
    {
        PrinterHealth.Online => StatusLevel.Success,
        PrinterHealth.Printing => StatusLevel.Info,
        PrinterHealth.Busy => StatusLevel.Info,
        PrinterHealth.Paused => StatusLevel.Warning,
        PrinterHealth.Offline => StatusLevel.Warning,
        PrinterHealth.PaperOut => StatusLevel.Warning,
        PrinterHealth.PaperJam => StatusLevel.Warning,
        PrinterHealth.TonerLow => StatusLevel.Warning,
        PrinterHealth.DoorOpen => StatusLevel.Warning,
        _ => StatusLevel.Error,
    };
}

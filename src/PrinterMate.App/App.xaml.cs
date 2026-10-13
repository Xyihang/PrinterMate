using System.Windows;
using System.Windows.Threading;
using PrinterMate.App.Services;

namespace PrinterMate.App;

/// <summary>
/// 全局异常兜底:任何未处理异常都必须被记录并显示在状态栏,绝不允许异常弹窗导致程序退出。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLog.Write("startup", "PrinterMate 界面已启动。");
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("dispatcher-exception", e.Exception?.ToString() ?? "(无异常对象)");
        StatusHub.Error(
            "界面异常",
            "发生了一个未处理的界面异常:" + (e.Exception?.Message ?? "未知错误"),
            "程序已阻止该异常导致退出。建议导出诊断报告后重启程序;若问题反复出现,请反馈日志目录中的文件。");
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        AppLog.Write("domain-exception", exception?.ToString() ?? e.ExceptionObject?.ToString() ?? "(无异常对象)");
        StatusHub.Error("后台异常", "发生了一个未处理的后台异常:" + (exception?.Message ?? "未知错误"));
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Write("task-exception", e.Exception?.ToString() ?? "(无异常对象)");
        StatusHub.Error("任务异常", "一个后台任务发生了未处理异常:" + (e.Exception?.InnerException?.Message ?? e.Exception?.Message ?? "未知错误"));
        e.SetObserved();
    }
}

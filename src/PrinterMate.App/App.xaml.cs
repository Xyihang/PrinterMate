using System.Windows;
using System.Windows.Threading;
using PrinterMate.App.Services;
using PrinterMate.App.Startup;

namespace PrinterMate.App;

/// <summary>
/// 全局异常兜底:任何未处理异常都必须被记录并显示在状态栏,绝不允许异常弹窗导致程序退出。
/// 同时负责显式控制启动顺序:启动动画 → 启动协议 → 主界面。
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

        // 关键:启动序列期间"最后一个窗口关闭"不能让程序退出 ——
        // 启动动画是当时唯一的窗口,它一关就会触发默认的 OnLastWindowClose 退出。
        // 因此启动期间改为显式退出,主窗口就绪后恢复默认语义。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!RunStartupSequence())
        {
            AppLog.Write("startup", "启动协议未获同意,程序退出。");
            Shutdown();
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        main.Show();
    }

    /// <summary>
    /// 启动序列:先放启动动画,再要启动协议同意。
    /// 任何一步自身异常都不影响主流程(动画失败直接跳过;协议失败按"不同意"处理,即拒绝进入)。
    /// </summary>
    private static bool RunStartupSequence()
    {
        try
        {
            new SplashWindow().ShowDialog();
        }
        catch (Exception ex)
        {
            // 动画只是观感,失败不应阻断启动
            AppLog.Write("splash-failed", ex.ToString());
        }

        try
        {
            return new AgreementWindow().ShowDialog() == true;
        }
        catch (Exception ex)
        {
            // 协议窗口打不开时按"未同意"处理:宁可不进入,也不静默跳过协议
            AppLog.Write("agreement-failed", ex.ToString());
            return false;
        }
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

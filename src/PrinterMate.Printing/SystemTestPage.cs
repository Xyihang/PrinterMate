using System.Diagnostics;
using System.IO;

namespace PrinterMate.Printing;

/// <summary>调用 Windows 自带的「打印机测试页」功能(printui.dll)。</summary>
public static class SystemTestPage
{
    private const int TimeoutMilliseconds = 30_000;

    /// <summary>调用 rundll32 printui.dll,PrintUIEntry /k /n "打印机名"。必须有超时与退出码检查。</summary>
    public static void Print(string printerName)
    {
        PrintingErrors.RequirePrinterName(printerName);

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
        startInfo.ArgumentList.Add("/k");
        startInfo.ArgumentList.Add("/n");
        startInfo.ArgumentList.Add(printerName);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "无法启动系统测试页程序(rundll32)。请确认系统组件 printui.dll 未被安全软件拦截。");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"启动系统测试页程序失败:{ex.Message}。请确认系统组件 printui.dll 未被安全软件拦截。", ex);
        }

        // 先异步抽干输出,避免子进程因管道写满而阻塞。
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeoutMilliseconds))
        {
            TryKill(process);
            throw new TimeoutException(
                $"系统测试页在 {TimeoutMilliseconds / 1000} 秒内未完成。请确认打印机「{printerName}」已开机且队列未暂停,然后重试。");
        }

        var output = SafeResult(stdout);
        var error = SafeResult(stderr);
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                $"系统测试页发送失败(退出码 {process.ExitCode})。{Flatten(detail)}请确认打印机「{printerName}」存在、驱动已安装且队列未被暂停。");
        }
    }

    private static string SafeResult(Task<string> task)
    {
        try
        {
            return task.GetAwaiter().GetResult() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 终止失败不影响主流程的错误提示。
        }
    }

    private static string Flatten(string text)
    {
        var trimmed = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return trimmed.Length == 0 ? string.Empty : trimmed + " ";
    }
}

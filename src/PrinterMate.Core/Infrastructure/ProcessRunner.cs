using System.Diagnostics;
using System.Text;

namespace PrinterMate.Core.Infrastructure;

/// <summary>外部进程执行结果。</summary>
public sealed record ProcessResult(
    string FileName,
    string Arguments,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;

    public string CombinedOutput => string.IsNullOrEmpty(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;

    public string ShortError
    {
        get
        {
            var text = CombinedOutput.Trim();
            return text.Length <= 800 ? text : text[..800] + "…";
        }
    }
}

/// <summary>
/// 外部进程执行器。统一处理超时、输出捕获与编码,
/// 供 pnputil / rundll32 / PowerShell 等调用复用。
/// </summary>
public static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    public static ProcessResult Run(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        bool captureOutput = true,
        Encoding? outputEncoding = null)
    {
        var argList = arguments.ToList();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            StandardOutputEncoding = outputEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = outputEncoding ?? Encoding.UTF8,
        };

        foreach (var a in argList)
        {
            psi.ArgumentList.Add(a);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(fileName, string.Join(' ', argList), -1, string.Empty, "进程无法启动。", sw.Elapsed, false);
            }
        }
        catch (Exception ex)
        {
            return new ProcessResult(fileName, string.Join(' ', argList), -1, string.Empty, ex.Message, sw.Elapsed, false);
        }

        var stdout = captureOutput ? new StringBuilder() : null;
        var stderr = captureOutput ? new StringBuilder() : null;

        if (captureOutput)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stdout!.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderr!.AppendLine(e.Data);
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        var limit = timeout ?? DefaultTimeout;
        var exited = process.WaitForExit((int)Math.Max(1000, limit.TotalMilliseconds));

        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程可能已自行退出,忽略。
            }

            sw.Stop();
            return new ProcessResult(
                fileName,
                string.Join(' ', argList),
                -1,
                stdout?.ToString() ?? string.Empty,
                $"命令执行超过 {limit.TotalSeconds:F0} 秒,已强制终止。{stderr}",
                sw.Elapsed,
                TimedOut: true);
        }

        process.WaitForExit();
        sw.Stop();

        return new ProcessResult(
            fileName,
            string.Join(' ', argList),
            process.ExitCode,
            stdout?.ToString() ?? string.Empty,
            stderr?.ToString() ?? string.Empty,
            sw.Elapsed,
            TimedOut: false);
    }

    public static Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        CancellationToken ct = default)
        => Task.Run(() => Run(fileName, arguments, timeout, workingDirectory), ct);
}

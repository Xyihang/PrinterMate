using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PrinterMate.Core.Drivers;
using PrinterMate.Core.Elevation;
using PrinterMate.Printing;

namespace PrinterMate.App.Services;

/// <summary>诊断报告导出:设备列表 + 驱动库状态 + 队列与作业 + 最近审计日志。</summary>
public static class DiagnosticsExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string BuildReport(AppServices services)
    {
        var localDevices = Safe(() => (object?)services.Enumerator.EnumerateAll().Value, "设备枚举失败");
        var usbDevices = Safe(() => (object?)services.UsbConnect?.ListUsbPrinters().Value, "USB 枚举失败");
        var libraryStatus = Safe(() => (object?)services.Library?.GetStatus(DriverMatchEngine.CurrentArchitecture), "驱动库状态读取失败");

        var queues = new List<object>();
        try
        {
            foreach (var printer in services.Queue.GetLocalPrinters())
            {
                object? jobs = null;
                try
                {
                    jobs = services.Queue.GetJobs(printer.Name);
                }
                catch (Exception ex)
                {
                    jobs = new { error = ex.Message };
                }

                PrinterHealth health;
                try
                {
                    health = services.Queue.GetHealth(printer.Name);
                }
                catch (Exception)
                {
                    health = PrinterHealth.Unknown;
                }

                queues.Add(new
                {
                    printer.Name,
                    printer.DriverName,
                    printer.PortName,
                    printer.IsNetwork,
                    printer.IsShared,
                    printer.JobCount,
                    printer.IsPaused,
                    健康状态 = PrinterStatusMapper.Describe(health),
                    处置建议 = PrinterStatusMapper.Suggest(health),
                    jobs,
                });
            }
        }
        catch (Exception ex)
        {
            queues.Add(new { error = "队列枚举失败:" + ex.Message });
        }

        var audit = new List<object>();
        try
        {
            if (services.Audit is not null)
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                foreach (var day in new[] { today.AddDays(-1), today })
                {
                    audit.AddRange(services.Audit.Read(day).Select(e => (object)new
                    {
                        e.Timestamp,
                        e.Operation,
                        e.Target,
                        e.Outcome,
                        e.Detail,
                        e.Sha256,
                        e.SignerSubject,
                        e.User,
                    }));
                }

                if (audit.Count > 300)
                {
                    audit = audit.Skip(audit.Count - 300).ToList();
                }
            }
        }
        catch (Exception ex)
        {
            audit.Add(new { error = "审计日志读取失败:" + ex.Message });
        }

        var report = new
        {
            生成时间 = DateTimeOffset.Now,
            应用 = new
            {
                名称 = "PrinterMate",
                版本 = typeof(DiagnosticsExporter).Assembly.GetName().Version?.ToString() ?? "未知",
                系统 = Environment.OSVersion.VersionString,
                架构 = DriverMatchEngine.CurrentArchitecture,
                已提权 = ElevatedLauncher.IsProcessElevated(),
                提权辅助进程 = ElevatedLauncher.HelperPath,
                提权辅助进程存在 = ElevatedLauncher.IsHelperAvailable,
            },
            启动错误 = services.StartupErrors,
            内置驱动库 = libraryStatus,
            本机打印资源 = localDevices,
            USB打印机 = usbDevices,
            打印队列与作业 = queues,
            最近审计日志 = audit,
        };

        try
        {
            return JsonSerializer.Serialize(report, Options);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "诊断报告序列化失败:" + ex.Message, detail = ex.ToString() }, Options);
        }
    }

    public static void WriteToFile(string path, AppServices services)
    {
        var content = BuildReport(services);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static object? Safe(Func<object?> factory, string errorLabel)
    {
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            return new { error = $"{errorLabel}:{ex.Message}" };
        }
    }
}

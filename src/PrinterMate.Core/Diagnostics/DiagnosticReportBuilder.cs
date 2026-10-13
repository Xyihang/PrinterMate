using System.Globalization;
using System.Text;
using PrinterMate.Catalog;
using PrinterMate.Core.Discovery;
using PrinterMate.Core.Drivers;
using PrinterMate.Core.Infrastructure;
using PrinterMate.Security;

namespace PrinterMate.Core.Diagnostics;

/// <summary>
/// 诊断报告生成器。汇总"设备信息 + 匹配到的驱动 + 端口 + 队列状态 + 最近审计日志",
/// 用于用户在遇到问题时一次性把上下文导出给我们。
/// </summary>
public sealed class DiagnosticReportBuilder
{
    private readonly AuditLog _audit;
    private readonly DriverLibrary _library;
    private readonly LocalPrinterEnumerator _enumerator;

    public DiagnosticReportBuilder(
        AuditLog? audit = null,
        DriverLibrary? library = null,
        LocalPrinterEnumerator? enumerator = null)
    {
        _audit = audit ?? new AuditLog();
        _library = library ?? DriverLibrary.Load();
        _enumerator = enumerator ?? new LocalPrinterEnumerator();
    }

    public OperationResult<string> Build()
    {
        var sb = new StringBuilder();
        var now = DateTimeOffset.Now;

        sb.AppendLine("================ PrinterMate 诊断报告 ================");
        sb.AppendLine($"生成时间:{now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();

        AppendEnvironment(sb);
        AppendDriverLibrary(sb);
        AppendUsbPrinters(sb);
        AppendQueues(sb);
        AppendAudit(sb);

        sb.AppendLine("================ 报告结束 ================");
        return OperationResult<string>.Ok(sb.ToString(), "诊断报告已生成。");
    }

    public OperationResult<string> Export(string destinationPath)
    {
        try
        {
            var report = Build();
            if (!report.IsSuccess || report.Value is null)
            {
                return report;
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(destinationPath, report.Value, new UTF8Encoding(true));
            return OperationResult<string>.Ok(destinationPath, $"诊断报告已导出到 {destinationPath}");
        }
        catch (Exception ex)
        {
            return ex.ToFailure<string>("导出诊断报告");
        }
    }

    private void AppendEnvironment(StringBuilder sb)
    {
        sb.AppendLine("---- 1. 运行环境 ----");
        sb.AppendLine($"操作系统      : {Environment.OSVersion.VersionString}");
        sb.AppendLine($"系统内部版本  : {Environment.OSVersion.Version.Build}");
        sb.AppendLine($"进程架构      : {DriverMatchEngine.CurrentArchitecture}");
        sb.AppendLine($"系统标识      : {DriverMatchEngine.CurrentOsTag}");
        sb.AppendLine($"机器名        : {Environment.MachineName}");
        sb.AppendLine($"PowerShell    : {PowerShellRunner.ExecutablePath ?? "(未找到)"}");
        sb.AppendLine($"提权辅助进程  : {Elevation.ElevatedLauncher.HelperPath}({(Elevation.ElevatedLauncher.IsHelperAvailable ? "存在" : "缺失")})");
        sb.AppendLine($"当前已提权    : {Elevation.ElevatedLauncher.IsProcessElevated()}");

        var wpp = new WppModeDetector().Detect();
        sb.AppendLine($"WPP 模式      : {(wpp.Enabled ? "已启用" : "未启用")}(来源:{wpp.Source})");
        sb.AppendLine($"WPP 详情      : {wpp.Detail}");
        sb.AppendLine();
    }

    private void AppendDriverLibrary(StringBuilder sb)
    {
        sb.AppendLine("---- 2. 内置驱动库 ----");
        sb.AppendLine($"驱动库根目录  : {_library.Root}");
        sb.AppendLine($"清单文件      : {_library.ManifestPath}");
        sb.AppendLine($"清单版本      : {_library.Manifest.LibraryVersion}");
        sb.AppendLine($"使用内置资源  : {_library.UsingEmbeddedManifest}");
        sb.AppendLine();

        var status = _library.GetStatus(DriverMatchEngine.CurrentArchitecture);
        sb.AppendLine($"条目总数      : {status.TotalEntries}(系统内置 {status.InboxEntries},需驱动包 {status.PackagedEntries})");
        sb.AppendLine($"可用条目      : {status.ReadyEntries}");
        sb.AppendLine($"覆盖品牌({status.Brands.Count}) : {string.Join(", ", status.Brands)}");

        if (status.MissingPackages.Count > 0)
        {
            sb.AppendLine("不可用条目及原因:");
            foreach (var item in status.MissingPackages.Take(60))
            {
                sb.AppendLine($"  - {item}");
            }

            if (status.MissingPackages.Count > 60)
            {
                sb.AppendLine($"  ...(其余 {status.MissingPackages.Count - 60} 条省略)");
            }
        }

        sb.AppendLine();
    }

    private void AppendUsbPrinters(StringBuilder sb)
    {
        sb.AppendLine("---- 3. USB 打印机 ----");
        try
        {
            var usb = UsbPrinterLocator.Locate(_enumerator);
            if (!usb.IsSuccess || usb.Value is null || usb.Value.Count == 0)
            {
                sb.AppendLine(usb.IsSuccess ? "(未检测到 USB 打印机)" : $"检测失败:{usb.Message}");
                sb.AppendLine();
                return;
            }

            foreach (var item in usb.Value)
            {
                sb.AppendLine($"设备    : {item.Device.DisplayName}");
                sb.AppendLine($"  型号  : {item.Device.Model}");
                sb.AppendLine($"  端口  : {item.PortName}");
                sb.AppendLine($"  状态  : {item.State} - {item.ActionText}");
                sb.AppendLine($"  队列  : {item.ExistingQueueName ?? "(无)"} / 驱动 {item.ExistingDriverName ?? "(无)"}");
                sb.AppendLine($"  硬件ID: {string.Join(" | ", item.Device.HardwareIds)}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"USB 枚举异常:{ex.Message}");
        }

        sb.AppendLine();
    }

    private void AppendQueues(StringBuilder sb)
    {
        sb.AppendLine("---- 4. 打印队列 ----");
        var queues = _enumerator.EnumeratePrintQueues();
        if (!queues.IsSuccess || queues.Value is null)
        {
            sb.AppendLine($"枚举失败:{queues.Message}");
            sb.AppendLine();
            return;
        }

        foreach (var queue in queues.Value)
        {
            sb.AppendLine($"{queue.DisplayName}");
            sb.AppendLine($"  驱动  : {queue.ExistingDriverName ?? "(未知)"}");
            sb.AppendLine($"  端口  : {queue.PortName}");
            sb.AppendLine($"  连接  : {queue.ConnectionText} / {queue.ReadinessText}");
        }

        sb.AppendLine();
        sb.AppendLine("---- 5. 打印驱动 ----");
        var drivers = _enumerator.EnumerateInstalledDrivers();
        if (!drivers.IsSuccess || drivers.Value is null)
        {
            sb.AppendLine($"枚举失败:{drivers.Message}");
        }
        else
        {
            foreach (var driver in drivers.Value)
            {
                sb.AppendLine($"{driver.Name} | {driver.PrinterEnvironment} | v{driver.DriverVersion} | {driver.InfPath}");
            }
        }

        sb.AppendLine();
    }

    private void AppendAudit(StringBuilder sb)
    {
        sb.AppendLine("---- 6. 审计日志(最近 40 条) ----");
        sb.AppendLine($"日志目录: {_audit.Directory}");
        try
        {
            var entries = _audit.Read(DateOnly.FromDateTime(DateTime.Now));
            foreach (var entry in entries.TakeLast(40))
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss} | {1} | {2} | {3} | {4} | sha256={5} | signer={6}",
                    entry.Timestamp,
                    entry.Operation,
                    entry.Outcome,
                    entry.Target,
                    Truncate(entry.Detail, 200),
                    string.IsNullOrWhiteSpace(entry.Sha256) ? "-" : entry.Sha256,
                    string.IsNullOrWhiteSpace(entry.SignerSubject) ? "-" : Truncate(entry.SignerSubject, 80)));
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"读取审计日志失败:{ex.Message}");
        }

        sb.AppendLine();
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + "…";
}

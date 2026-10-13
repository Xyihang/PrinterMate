using System.Text;
using System.Text.RegularExpressions;
using PrinterMate.Core;
using PrinterMate.Core.Discovery;

namespace PrinterMate.App.Services;

/// <summary>连接/安装相关的公共构造逻辑。</summary>
public static class InstallHelper
{
    private static readonly Regex NonPortChar = new(@"[^A-Za-z0-9]", RegexOptions.Compiled);

    /// <summary>把 InstallPlan 拼成给用户二次确认的风险提示全文。</summary>
    public static string BuildDisclosure(InstallPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("即将执行以下操作:");
        builder.AppendLine($"  · 打印队列名称:{plan.QueueName}");
        builder.AppendLine($"  · 使用的驱动:{plan.DriverName}({SourceText(plan.Source)})");
        builder.AppendLine($"  · 端口:{plan.Port.KindText} {plan.Port.PortName}".TrimEnd());
        builder.AppendLine($"  · 需要管理员权限:{(plan.RequiresElevation ? "是(会弹出 UAC 确认框)" : "否")}");
        builder.AppendLine($"  · 需要联网下载:{(plan.RequiresDownload ? "是" : "否")}");

        if (plan.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("注意事项:");
            foreach (var warning in plan.Warnings)
            {
                builder.AppendLine("  · " + warning);
            }
        }

        builder.AppendLine();
        builder.AppendLine("风险提示:");
        builder.AppendLine(plan.RiskDisclosure);
        return builder.ToString();
    }

    /// <summary>根据连接方式推导端口规格(USB 走 PnP 既有端口,网络走新建端口)。</summary>
    public static PortSpec BuildPortSpec(DiscoveredPrinter device)
    {
        var address = DeviceNameParser.ExtractAddress(device.Address) ?? device.Address;
        if (string.IsNullOrWhiteSpace(address))
        {
            address = DeviceNameParser.ExtractAddress(device.PortName) ?? string.Empty;
        }

        var safe = NonPortChar.Replace(address, "_").Trim('_');

        return device.Connection switch
        {
            ConnectionKind.Ipp => new PortSpec(PortKind.Ipp, Prefix("IP", safe), address, 631),
            ConnectionKind.Wsd => new PortSpec(PortKind.RawTcp, Prefix("RAW", safe), address, 9100),
            ConnectionKind.RawTcp => new PortSpec(PortKind.RawTcp, Prefix("RAW", safe), address, 9100),
            ConnectionKind.Lpd => new PortSpec(PortKind.Lpd, Prefix("LPD", safe), address, 515),
            ConnectionKind.Shared => new PortSpec(PortKind.Shared, string.Empty, null, null, device.Address),
            ConnectionKind.Usb => new PortSpec(PortKind.Usb, device.PortName),
            _ => new PortSpec(PortKind.Existing, device.PortName),
        };
    }

    /// <summary>把人工选择的驱动包装成一个 DriverDecision,便于统一展示。</summary>
    public static DriverDecision ManualDecision(DriverCandidate candidate) => new(
        candidate.Installable ? OperationStatus.Success : OperationStatus.NeedsUserAction,
        candidate.SourceText,
        candidate,
        new[] { candidate },
        $"已使用人工选择的驱动「{candidate.DriverName}」({candidate.PdlText})。",
        "若打印出现乱码或字体缺失,请改用同型号的其它 PDL 驱动后重试。",
        candidate.Source == DriverSource.Online);

    public static string SourceText(DriverSource source) => source switch
    {
        DriverSource.ClassDriver => "L0 系统免驱",
        DriverSource.BuiltinUpd => "L1 内置驱动",
        DriverSource.Online => "L2 在线源",
        _ => "无可用方案",
    };

    /// <summary>根据驱动名粗略推断页面描述语言,用于自绘测试页展示。</summary>
    public static string GuessPdl(string? driverName)
    {
        if (string.IsNullOrWhiteSpace(driverName))
        {
            return "未知";
        }

        if (driverName.Contains("PCL6", StringComparison.OrdinalIgnoreCase))
        {
            return "PCL6";
        }

        if (driverName.Contains("PCL", StringComparison.OrdinalIgnoreCase))
        {
            return "PCL";
        }

        if (driverName.Contains("PostScript", StringComparison.OrdinalIgnoreCase)
            || driverName.Contains("PS3", StringComparison.OrdinalIgnoreCase))
        {
            return "PostScript";
        }

        if (driverName.Contains("Class Driver", StringComparison.OrdinalIgnoreCase))
        {
            return "系统类驱动";
        }

        if (driverName.Contains("Universal", StringComparison.OrdinalIgnoreCase))
        {
            return "通用驱动";
        }

        return "未知";
    }

    public static string DescribeCandidate(DriverCandidate candidate)
    {
        var state = candidate.Installable ? "可用" : $"不可用({candidate.BlockReason})";
        return $"{candidate.SourceText} | {candidate.DriverName} | {candidate.PdlText} | 置信度 {candidate.ConfidenceText} | {state}";
    }

    private static string Prefix(string prefix, string value)
        => string.IsNullOrWhiteSpace(value) ? prefix : $"{prefix}_{value}";
}

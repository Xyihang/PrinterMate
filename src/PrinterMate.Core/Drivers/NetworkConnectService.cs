using System.Net;
using PrinterMate.Core.Discovery;

namespace PrinterMate.Core.Drivers;

/// <summary>网络连接计划。</summary>
public sealed record NetworkConnectPlan(
    DiscoveredPrinter Printer,
    DriverDecision Decision,
    InstallPlan? Plan,
    string Summary,
    string Suggestion)
{
    public bool CanProceed => Plan is not null && Decision.Status == OperationStatus.Success;
}

/// <summary>
/// 网络路径(IPP / RAW / LPD / 共享)。作为 USB 主场景之外的补充通道,
/// 免驱设备在这里体验最好:IPP 设备可以直接用系统类驱动建队列。
/// </summary>
public sealed class NetworkConnectService
{
    private readonly DriverMatchEngine _engine;
    private readonly PrinterInstaller _installer;

    public NetworkConnectService(DriverMatchEngine engine, PrinterInstaller installer)
    {
        _engine = engine;
        _installer = installer;
    }

    /// <summary>局域网发现。</summary>
    public async Task<OperationResult<IReadOnlyList<DiscoveredPrinter>>> ScanAsync(
        DiscoveryOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            var service = new NetworkDiscoveryService();
            return await service.DiscoverAllAsync(options, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ex.ToFailure<IReadOnlyList<DiscoveredPrinter>>("扫描局域网打印机");
        }
    }

    /// <summary>手动构造一个网络目标。</summary>
    public static OperationResult<DiscoveredPrinter> CreateManualTarget(string hostOrIp, ConnectionKind protocol, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(hostOrIp))
        {
            return OperationResult<DiscoveredPrinter>.Fail("请输入打印机 IP 地址或主机名。", "例如 192.168.1.20 或 printer01。");
        }

        var target = hostOrIp.Trim();
        var address = target;
        var port = protocol switch
        {
            ConnectionKind.Ipp => 631,
            ConnectionKind.Lpd => 515,
            ConnectionKind.RawTcp => 9100,
            _ => 0,
        };

        // 允许用户直接填写 host:port
        if (protocol != ConnectionKind.Shared)
        {
            var parts = target.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var customPort))
            {
                address = parts[0];
                port = customPort;
            }
            else if (parts.Length != 1)
            {
                return OperationResult<DiscoveredPrinter>.Fail($"无法解析地址「{target}」。", "支持 host、host:port 与 \\\\主机\\共享名 三种写法。");
            }
        }

        if (protocol == ConnectionKind.Shared)
        {
            if (!target.StartsWith(@"\\", StringComparison.Ordinal) || target.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            {
                return OperationResult<DiscoveredPrinter>.Fail($"共享路径「{target}」格式不正确。", @"正确写法:\\\\主机名\\共享打印机名。");
            }
        }
        else if (!IPAddress.TryParse(address, out _) && address.Contains(' '))
        {
            return OperationResult<DiscoveredPrinter>.Fail($"地址「{address}」不合法。", "请输入合法的 IP 地址或主机名。");
        }

        var name = string.IsNullOrWhiteSpace(displayName) ? $"{protocol} {target}" : displayName!;
        var isIpp = protocol == ConnectionKind.Ipp;

        var printer = new DiscoveredPrinter
        {
            Id = $"manual:{protocol}:{target}",
            DisplayName = name,
            Model = name,
            Connection = protocol,
            Address = address,
            PortName = isIpp ? $"ipp://{address}:{port}/ipp/print" : address,
            IppUri = isIpp ? $"ipp://{address}:{port}/ipp/print" : null,
            SupportsIpp = isIpp,
            SupportsRaw9100 = protocol == ConnectionKind.RawTcp,
            SupportsLpd = protocol == ConnectionKind.Lpd,
            DiscoverySource = "手动添加",
            Readiness = DriverReadiness.Unknown,
        };

        return OperationResult<DiscoveredPrinter>.Ok(printer, $"已添加手动目标「{name}」。");
    }

    /// <summary>为网络打印机准备连接计划。</summary>
    public OperationResult<NetworkConnectPlan> Prepare(DiscoveredPrinter printer, string? queueName = null)
    {
        if (printer.ExistingQueueName is not null && printer.Readiness == DriverReadiness.Installed)
        {
            return OperationResult<NetworkConnectPlan>.Ok(
                new NetworkConnectPlan(printer, DriverDecision.Unresolved("已安装", string.Empty), null,
                    $"打印机「{printer.DisplayName}」已存在队列「{printer.ExistingQueueName}」。",
                    "可直接打印测试页验证。"),
                "该设备已有可用队列。");
        }

        var decision = _engine.Decide(printer, preferDriverFree: true);
        if (decision.Selected is null)
        {
            var suggestion = decision.Candidates.Count(c => c.Installable) > 1
                ? "该型号对应多个驱动,请在驱动管理页手动选择后再连接。"
                : decision.Suggestion;

            return new OperationResult<NetworkConnectPlan>
            {
                Status = decision.Status,
                Message = decision.Message,
                Suggestion = suggestion,
                Value = new NetworkConnectPlan(printer, decision, null, decision.Message, suggestion),
            };
        }

        var port = BuildPortSpec(printer);
        var targetQueue = queueName ?? BuildQueueName(printer);

        var plan = _installer.BuildPlan(printer, decision.Selected, port, targetQueue);
        if (!plan.IsSuccess || plan.Value is null)
        {
            return new OperationResult<NetworkConnectPlan>
            {
                Status = plan.Status,
                Message = plan.Message,
                Suggestion = plan.Suggestion,
                Value = new NetworkConnectPlan(printer, decision, null, plan.Message, plan.Suggestion ?? decision.Suggestion),
            };
        }

        return OperationResult<NetworkConnectPlan>.Ok(
            new NetworkConnectPlan(printer, decision, plan.Value, decision.Message, decision.Suggestion),
            decision.Message);
    }

    /// <summary>执行连接。</summary>
    public OperationResult<InstallReport> Connect(NetworkConnectPlan plan, bool printTestPage, IProgress<InstallStepResult>? progress = null)
    {
        if (plan.Plan is null)
        {
            return OperationResult<InstallReport>.Fail(
                string.IsNullOrWhiteSpace(plan.Summary) ? "该设备当前不需要安装驱动。" : plan.Summary,
                plan.Suggestion);
        }

        var install = _installer.Install(plan.Plan, progress);
        var report = install.Value;

        if (install.IsSuccess && printTestPage && report is not null)
        {
            var test = _installer.PrintSystemTestPage(report.QueueName);
            var steps = report.Steps.ToList();
            steps.Add(new InstallStepResult(
                InstallStep.PrintTestPage,
                test.IsSuccess ? InstallStepState.Succeeded : InstallStepState.Failed,
                test.Message,
                "系统测试页",
                test.IsSuccess ? 0 : -1,
                test.Suggestion ?? string.Empty,
                TimeSpan.Zero));

            report = report with
            {
                Steps = steps,
                Success = report.Success && test.IsSuccess,
                Summary = report.Summary + " " + test.Message,
            };
        }

        return install with { Value = report };
    }

    /// <summary>按连接方式构造端口规格。</summary>
    public static PortSpec BuildPortSpec(DiscoveredPrinter printer)
    {
        var host = string.IsNullOrWhiteSpace(printer.Address) ? printer.Model : printer.Address;

        return printer.Connection switch
        {
            ConnectionKind.Ipp => new PortSpec(PortKind.Ipp, $"ipp://{host}:631/ipp/print", host, 631),
            ConnectionKind.Lpd => new PortSpec(PortKind.Lpd, host, host, 515),
            ConnectionKind.Shared => new PortSpec(PortKind.Shared, printer.PortName, null, null, printer.PortName),
            ConnectionKind.Wsd when !string.IsNullOrWhiteSpace(printer.PortName) => new PortSpec(PortKind.Existing, printer.PortName),
            _ => new PortSpec(PortKind.RawTcp, host, host, 9100),
        };
    }

    private static string BuildQueueName(DiscoveredPrinter printer)
    {
        var baseName = string.IsNullOrWhiteSpace(printer.Model) ? printer.DisplayName : printer.Model;
        baseName = DeviceNameParser.CleanModelName(baseName);
        return string.IsNullOrWhiteSpace(printer.Address) ? baseName : $"{baseName} ({printer.Address})";
    }
}

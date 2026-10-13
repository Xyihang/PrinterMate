using PrinterMate.Catalog;
using PrinterMate.Core;
using PrinterMate.Core.Diagnostics;
using PrinterMate.Core.Discovery;
using PrinterMate.Core.Drivers;
using PrinterMate.Core.Elevation;
using PrinterMate.Core.Infrastructure;
using PrinterMate.Printing;
using PrinterMate.Security;
using Xunit;
using Xunit.Abstractions;

namespace PrinterMate.IntegrationTests;

/// <summary>
/// 真机冒烟测试:直接对当前机器的打印子系统取证。
/// 这些用例不做破坏性操作(不安装驱动、不建队列、不打印),
/// 只读枚举 + 决策,用于验证"发现 → 匹配 → 计划"这条链路在真实环境可用。
/// </summary>
public class LocalMachineSmokeTests
{
    private readonly ITestOutputHelper _output;

    public LocalMachineSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void 环境探测_PowerShell与WPP与提权辅助进程()
    {
        _output.WriteLine($"PowerShell          : {PowerShellRunner.ExecutablePath ?? "(未找到)"}");
        _output.WriteLine($"进程架构            : {DriverMatchEngine.CurrentArchitecture}");
        _output.WriteLine($"系统标识            : {DriverMatchEngine.CurrentOsTag}");
        _output.WriteLine($"OS 版本             : {Environment.OSVersion.VersionString} (build {Environment.OSVersion.Version.Build})");

        var wpp = new WppModeDetector().Detect();
        _output.WriteLine($"WPP 模式            : Enabled={wpp.Enabled} Source={wpp.Source}");
        _output.WriteLine($"WPP 详情            : {wpp.Detail}");
        _output.WriteLine($"提权辅助进程        : {ElevatedLauncher.HelperPath}");
        _output.WriteLine($"辅助进程存在        : {ElevatedLauncher.IsHelperAvailable}");
        _output.WriteLine($"当前已提权          : {ElevatedLauncher.IsProcessElevated()}");

        Assert.False(string.IsNullOrWhiteSpace(DriverMatchEngine.CurrentArchitecture));
    }

    [Fact]
    public void 枚举本机打印队列_必须成功且字段完整()
    {
        var enumerator = new LocalPrinterEnumerator();
        var result = enumerator.EnumeratePrintQueues();

        _output.WriteLine($"结果:{result.Message}");
        Assert.True(result.IsSuccess, result.Message + " " + result.Suggestion);

        foreach (var queue in result.Value ?? Array.Empty<DiscoveredPrinter>())
        {
            _output.WriteLine($"  {queue.DisplayName,-40} 端口={queue.PortName,-12} 驱动={queue.ExistingDriverName,-32} 连接={queue.ConnectionText} 状态={queue.ReadinessText} 厂商={queue.Vendor}");
        }

        Assert.NotNull(result.Value);
    }

    [Fact]
    public void 枚举PnP打印设备_必须成功()
    {
        var result = new LocalPrinterEnumerator().EnumeratePnpDevices();

        _output.WriteLine($"结果:{result.Message}");
        Assert.True(result.IsSuccess, result.Message);

        foreach (var device in result.Value ?? Array.Empty<DiscoveredPrinter>())
        {
            _output.WriteLine($"  {device.DisplayName,-40} 端口={device.PortName,-10} 连接={device.ConnectionText}");
            _output.WriteLine($"      硬件ID: {string.Join(" | ", device.HardwareIds)}");
        }
    }

    [Fact]
    public void USB打印机定位_应给出端口与绑定状态()
    {
        var result = UsbPrinterLocator.Locate();

        _output.WriteLine($"结果:{result.Message}");
        Assert.True(result.IsSuccess, result.Message);

        foreach (var usb in result.Value ?? Array.Empty<UsbPrinterInfo>())
        {
            _output.WriteLine($"  {usb.Device.DisplayName}");
            _output.WriteLine($"      端口={usb.PortName} 状态={usb.State} 队列={usb.ExistingQueueName ?? "(无)"} 驱动={usb.ExistingDriverName ?? "(无)"}");
            _output.WriteLine($"      {usb.ActionText}");

            if (usb.State != UsbBindingState.Unknown)
            {
                Assert.False(string.IsNullOrWhiteSpace(usb.PortName), "已知状态的 USB 打印机必须有端口名");
            }
        }
    }

    [Fact]
    public void USB打印机_驱动决策必须给出可解释结论()
    {
        var located = UsbPrinterLocator.Locate();
        if (located.Value is null || located.Value.Count == 0)
        {
            _output.WriteLine("本机没有 USB 打印机,跳过决策验证。");
            return;
        }

        var engine = new DriverMatchEngine(DriverLibrary.Load(), new WppModeDetector());

        foreach (var usb in located.Value)
        {
            var decision = engine.Decide(usb.Device);
            _output.WriteLine($"  {usb.Device.DisplayName}");
            _output.WriteLine($"      层={decision.Layer} 状态={decision.Status} 需下载={decision.RequiresDownload}");
            _output.WriteLine($"      结论={decision.Message}");
            _output.WriteLine($"      建议={decision.Suggestion}");

            foreach (var candidate in decision.Candidates.Take(4))
            {
                _output.WriteLine($"      候选:{candidate.DriverName} [{candidate.SourceText}] 可安装={candidate.Installable} 原因={candidate.BlockReason ?? "-"}");
            }

            Assert.False(string.IsNullOrWhiteSpace(decision.Message));
            Assert.False(string.IsNullOrWhiteSpace(decision.Suggestion));
        }
    }

    [Fact]
    public void 驱动库状态_必须可读且区分内置与待入库()
    {
        var library = DriverLibrary.Load();
        var status = library.GetStatus(DriverMatchEngine.CurrentArchitecture);

        _output.WriteLine($"根目录       : {status.Root}");
        _output.WriteLine($"清单文件     : {status.ManifestPath}");
        _output.WriteLine($"清单版本     : {status.LibraryVersion}(内置资源={status.UsingEmbeddedManifest})");
        _output.WriteLine($"条目         : 总 {status.TotalEntries} / 系统内置 {status.InboxEntries} / 需驱动包 {status.PackagedEntries} / 可用 {status.ReadyEntries}");
        _output.WriteLine($"覆盖品牌     : {string.Join(", ", status.Brands)}");
        _output.WriteLine($"不可用条目数 : {status.MissingPackages.Count}");

        foreach (var missing in status.MissingPackages.Take(5))
        {
            _output.WriteLine($"  - {missing}");
        }

        Assert.True(status.InboxEntries >= 2, "至少应有 IPP 与 PS 两个系统内置类驱动条目");
        Assert.True(status.Brands.Count >= 15, $"品牌覆盖不足:{status.Brands.Count}");
    }

    [Fact]
    public void 安装计划生成_必须包含风险告知与提权标记()
    {
        var located = UsbPrinterLocator.Locate();
        if (located.Value is null || located.Value.Count == 0)
        {
            _output.WriteLine("本机没有 USB 打印机,跳过安装计划验证。");
            return;
        }

        var library = DriverLibrary.Load();
        var engine = new DriverMatchEngine(library, new WppModeDetector());
        var installer = new PrinterInstaller(library);

        foreach (var usb in located.Value)
        {
            var decision = engine.Decide(usb.Device);
            if (decision.Selected is null)
            {
                _output.WriteLine($"{usb.Device.DisplayName}:无可直接使用的驱动候选({decision.Message})");
                continue;
            }

            var port = UsbPrinterLocator.BuildPortSpec(usb.PortName, portExists: usb.State == UsbBindingState.Ready || usb.State == UsbBindingState.NeedsQueue);
            var plan = installer.BuildPlan(usb.Device, decision.Selected, port);

            _output.WriteLine($"{usb.Device.DisplayName}:计划成功={plan.IsSuccess} {plan.Message}");
            if (plan.Value is not null)
            {
                _output.WriteLine($"      队列={plan.Value.QueueName} 驱动={plan.Value.DriverName} 来源={plan.Value.Source}");
                _output.WriteLine($"      端口={plan.Value.Port.Kind}/{plan.Value.Port.PortName} 需提权={plan.Value.RequiresElevation} 需下载={plan.Value.RequiresDownload}");
                _output.WriteLine($"      风险告知={plan.Value.RiskDisclosure.Replace('\n', ' ')}");
                Assert.False(string.IsNullOrWhiteSpace(plan.Value.RiskDisclosure));
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(plan.Suggestion));
            }
        }
    }

    [Fact]
    public void 队列健康状态_应能读取并给出中文建议()
    {
        var queueService = new PrintQueueService();
        var printers = queueService.GetLocalPrinters();

        _output.WriteLine($"本机队列数:{printers.Count}");
        Assert.NotEmpty(printers);

        foreach (var printer in printers.Take(6))
        {
            var health = queueService.GetHealth(printer.Name);
            var jobs = queueService.GetJobs(printer.Name);

            _output.WriteLine($"  {printer.Name,-40} 驱动={printer.DriverName,-30} 健康={health} 说明={PrinterStatusMapper.Describe(health)}");
            _output.WriteLine($"      作业数={jobs.Count} 建议={PrinterStatusMapper.Suggest(health)}");

            Assert.False(string.IsNullOrWhiteSpace(PrinterStatusMapper.Describe(health)));
            Assert.False(string.IsNullOrWhiteSpace(PrinterStatusMapper.Suggest(health)));
        }
    }

    [Fact]
    public void 诊断报告_必须能生成且包含关键章节()
    {
        var builder = new DiagnosticReportBuilder();
        var report = builder.Build();

        Assert.True(report.IsSuccess, report.Message);
        var text = report.Value!;

        foreach (var section in new[] { "运行环境", "内置驱动库", "USB 打印机", "打印队列", "审计日志" })
        {
            Assert.Contains(section, text);
        }

        _output.WriteLine($"报告长度:{text.Length} 字符");
        _output.WriteLine(text.Split('\n').Take(14).Aggregate((a, b) => a + "\n" + b));
    }

    [Fact]
    public async Task 微软更新目录检索_应能解析出驱动条目()
    {
        // 先探活,避免离线环境下把网络问题误判成解析失败
        var reachable = true;
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var response = await probe.GetAsync("https://www.catalog.update.microsoft.com/Search.aspx?q=test");
            reachable = response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            reachable = false;
            _output.WriteLine($"Microsoft Update Catalog 不可达,跳过断言:{ex.Message}");
        }

        if (!reachable)
        {
            return;
        }

        var service = new OnlineDriverService();
        var result = await service.SearchAsync("HP", "HP Deskjet 2520 series");

        _output.WriteLine($"检索结果:{result.Message}");

        Assert.True(result.IsSuccess, $"线上检索失败:{result.Message} / {result.Suggestion}");
        Assert.NotEmpty(result.Value!);

        foreach (var candidate in result.Value!.Take(5))
        {
            _output.WriteLine($"  {candidate.DisplayName}");
            _output.WriteLine($"      版本={candidate.Version} 大小={candidate.Size}");
            _output.WriteLine($"      {candidate.Note}");
        }
    }

    [Fact]
    public void 白名单与审计日志_在本机可正常初始化()
    {
        var allowlist = DownloadSourceAllowlist.Default;
        _output.WriteLine($"微软来源:{allowlist.MicrosoftDomains.Count} 厂商域名:{allowlist.VendorDomains.Count}");

        var check = allowlist.Check("https://www.catalog.update.microsoft.com/Search.aspx?q=hp");
        _output.WriteLine($"MUC 校验:{check.IsAllowed} {check.Reason}");
        Assert.True(check.IsAllowed);

        var bad = allowlist.Check("https://driverscollection.com/hp.exe");
        _output.WriteLine($"非白名单校验:{bad.IsAllowed} {bad.Reason}");
        Assert.False(bad.IsAllowed);

        var audit = new AuditLog();
        _output.WriteLine($"审计日志目录:{audit.Directory}");
        Assert.False(string.IsNullOrWhiteSpace(audit.Directory));
    }
}

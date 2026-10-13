using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PrinterMate.Core;
using PrinterMate.Core.Discovery;
using Xunit;

namespace PrinterMate.UnitTests.Discovery;

public sealed class DiscoveryOptionsTests
{
    [Fact]
    public void Default_总超时为8秒且全部发现器启用()
    {
        var options = DiscoveryOptions.Default;

        Assert.Equal(TimeSpan.FromSeconds(8), options.Timeout);
        Assert.True(options.EnableSnmp);
        Assert.True(options.EnableMdns);
        Assert.True(options.EnableWsd);
        Assert.True(options.EnableIppProbe);
        Assert.True(options.EnableRawProbe);
        Assert.Null(options.Subnets);
    }

    [Fact]
    public void Default_每次读取返回同一个实例() => Assert.Same(DiscoveryOptions.Default, DiscoveryOptions.Default);
}

public sealed class NetworkAddressTests
{
    [Fact]
    public void ComputeBroadcast_按掩码计算定向广播地址()
    {
        Assert.Equal("192.168.1.255", NetworkAddresses.ComputeBroadcast(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0"))!.ToString());
        Assert.Equal("10.15.255.255", NetworkAddresses.ComputeBroadcast(IPAddress.Parse("10.15.3.7"), IPAddress.Parse("255.255.0.0"))!.ToString());
    }

    [Fact]
    public void ComputeBroadcast_IPv6或非法输入_返回null()
    {
        Assert.Null(NetworkAddresses.ComputeBroadcast(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback));
        Assert.Null(NetworkAddresses.ComputeBroadcast(null!, null!));
    }

    [Fact]
    public void GetLocalBroadcastAddresses_不抛异常且仅返回IPv4()
    {
        var addresses = NetworkDiscoveryService.GetLocalBroadcastAddresses();

        Assert.NotNull(addresses);
        Assert.All(addresses, address => Assert.Equal(AddressFamily.InterNetwork, address.AddressFamily));
    }

    [Fact]
    public void GetLocalBroadcastAddresses_多次调用结果稳定()
    {
        var first = NetworkDiscoveryService.GetLocalBroadcastAddresses().Select(a => a.ToString()).ToArray();
        var second = NetworkAddresses.GetLocalBroadcastAddresses().Select(a => a.ToString()).ToArray();

        Assert.Equal(first, second);
    }
}

public sealed class DiscoveredPrinterMergerTests
{
    [Fact]
    public void Merge_同IP多来源_合并为一条并保留最全信息()
    {
        var snmp = new DiscoveredPrinter
        {
            Id = "net:192.168.1.20",
            DisplayName = "OFFICE-HP",
            Address = "192.168.1.20",
            Vendor = "HP",
            DiscoverySource = "SNMP",
            Notes = "HP LaserJet Pro M404dn",
        };

        var mdns = new DiscoveredPrinter
        {
            Id = "net:192.168.1.20",
            DisplayName = "Front Desk",
            Address = "192.168.1.20",
            Model = "LaserJet Pro M404dn",
            Vendor = "HP",
            IppUri = "ipp://192.168.1.20:631/ipp/print",
            IsMopria = true,
            SupportsIpp = true,
            Readiness = DriverReadiness.DriverFree,
            DiscoverySource = "mDNS",
        };

        var merged = Assert.Single(DiscoveredPrinterMerger.Merge([snmp, mdns]));

        Assert.Equal("LaserJet Pro M404dn", merged.Model);
        Assert.Equal("HP", merged.Vendor);
        Assert.Equal("192.168.1.20", merged.Address);
        Assert.Equal("ipp://192.168.1.20:631/ipp/print", merged.IppUri);
        Assert.Equal("HP LaserJet Pro M404dn", merged.Notes);
        Assert.True(merged.IsMopria);
        Assert.True(merged.SupportsIpp);
        Assert.Equal(DriverReadiness.DriverFree, merged.Readiness);
        Assert.Equal("SNMP+mDNS", merged.DiscoverySource);
    }

    [Fact]
    public void Merge_不同IP_各自保留()
    {
        var left = new DiscoveredPrinter { Id = "net:10.0.0.1", DisplayName = "A", Address = "10.0.0.1" };
        var right = new DiscoveredPrinter { Id = "net:10.0.0.2", DisplayName = "B", Address = "10.0.0.2" };

        Assert.Equal(2, DiscoveredPrinterMerger.Merge([left, right]).Count);
    }

    [Fact]
    public void Merge_带端口的结果与纯IP结果_归并到一起()
    {
        var withPort = new DiscoveredPrinter { Id = "net:10.0.0.9:631", DisplayName = "A", Address = "10.0.0.9", SupportsIpp = true };
        var plain = new DiscoveredPrinter { Id = "net:10.0.0.9", DisplayName = "A", Address = "10.0.0.9", SupportsRaw9100 = true };

        var merged = Assert.Single(DiscoveredPrinterMerger.Merge([withPort, plain]));

        Assert.True(merged.SupportsIpp);
        Assert.True(merged.SupportsRaw9100);
    }

    [Fact]
    public void Completeness_信息越多得分越高()
    {
        var bare = new DiscoveredPrinter { Id = "net:1.1.1.1", DisplayName = "bare", Address = "1.1.1.1" };
        var rich = new DiscoveredPrinter { Id = "net:1.1.1.1", DisplayName = "rich", Address = "1.1.1.1", Model = "M", Vendor = "V", IsMopria = true };

        Assert.True(DiscoveredPrinterMerger.Completeness(rich) > DiscoveredPrinterMerger.Completeness(bare));
    }
}

public sealed class NetworkDiscoveryServiceTests
{
    private sealed class FakeProvider(
        string name,
        IReadOnlyList<DiscoveredPrinter> printers,
        TimeSpan? delay = null,
        bool honorCancellation = true) : INetworkDiscovery
    {
        public string Name => name;

        public void Dispose()
        {
        }

        public async Task<IReadOnlyList<DiscoveredPrinter>> DiscoverAsync(DiscoveryOptions options, CancellationToken ct)
        {
            if (delay is { } wait)
            {
                await Task.Delay(wait, honorCancellation ? ct : CancellationToken.None);
            }

            return printers;
        }
    }

    [Fact]
    public void ProviderNames_返回注入的发现器名称()
    {
        var service = new NetworkDiscoveryService([new FakeProvider("测试发现器", [])]);

        Assert.Equal(new[] { "测试发现器" }, service.ProviderNames.ToArray());
    }

    [Fact]
    public void 未注入发现器时_使用三个内置发现器()
    {
        var service = new NetworkDiscoveryService();

        Assert.Equal(new[] { "SNMP 广播", "mDNS / DNS-SD", "WS-Discovery" }, service.ProviderNames.ToArray());
    }

    [Fact]
    public async Task DiscoverAllAsync_并发汇总并去重()
    {
        var snmpPrinter = new DiscoveredPrinter
        {
            Id = DiscoveryText.BuildNetworkId("192.168.1.20"),
            DisplayName = "OFFICE-HP",
            Address = "192.168.1.20",
            Vendor = "HP",
            DiscoverySource = "SNMP",
            Notes = "HP LaserJet Pro M404dn",
        };
        var mdnsPrinter = new DiscoveredPrinter
        {
            Id = DiscoveryText.BuildNetworkId("192.168.1.20"),
            DisplayName = "Front Desk",
            Address = "192.168.1.20",
            Model = "LaserJet Pro M404dn",
            Vendor = "HP",
            IppUri = "ipp://192.168.1.20:631/ipp/print",
            IsMopria = true,
            SupportsIpp = true,
            Readiness = DriverReadiness.DriverFree,
            DiscoverySource = "mDNS",
        };

        var service = new NetworkDiscoveryService([new FakeProvider("A", [snmpPrinter]), new FakeProvider("B", [mdnsPrinter])]);

        var result = await service.DiscoverAllAsync(new DiscoveryOptions(TimeSpan.FromSeconds(2)));

        Assert.True(result.IsSuccess);
        var printer = Assert.Single(result.Value!);
        Assert.Equal("192.168.1.20", printer.Address);
        Assert.Equal("LaserJet Pro M404dn", printer.Model);
        Assert.Equal("HP LaserJet Pro M404dn", printer.Notes);
        Assert.Equal(DriverReadiness.DriverFree, printer.Readiness);
        Assert.Equal("SNMP+mDNS", printer.DiscoverySource);
    }

    [Fact]
    public async Task DiscoverAllAsync_发现器抛异常_不影响其它发现器()
    {
        var service = new NetworkDiscoveryService([new ThrowingProvider(), new FakeProvider("B", [Printer("10.0.0.5", "M", "Canon", "mDNS")])]);

        var result = await service.DiscoverAllAsync(new DiscoveryOptions(TimeSpan.FromSeconds(2)));

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
    }

    [Fact]
    public async Task DiscoverAllAsync_发现器挂起_仍在总超时内返回()
    {
        var stuck = new FakeProvider("挂起", [], TimeSpan.FromSeconds(30), honorCancellation: false);
        var quick = new FakeProvider("快", [Printer("10.0.0.5", "M", "Canon", "mDNS")]);
        var service = new NetworkDiscoveryService([stuck, quick]);

        var stopwatch = Stopwatch.StartNew();
        var result = await service.DiscoverAllAsync(new DiscoveryOptions(TimeSpan.FromMilliseconds(400)));
        stopwatch.Stop();

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"耗时 {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task DiscoverAllAsync_无发现器_返回空列表且成功()
    {
        var service = new NetworkDiscoveryService([]);

        var result = await service.DiscoverAllAsync(new DiscoveryOptions(TimeSpan.FromSeconds(1)));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task DiscoverAllAsync_预先取消_返回取消状态()
    {
        var service = new NetworkDiscoveryService([new FakeProvider("A", [])]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await service.DiscoverAllAsync(new DiscoveryOptions(TimeSpan.FromSeconds(1)), null, cts.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task DiscoverHostAsync_空输入_提示用户补充()
    {
        var service = new NetworkDiscoveryService([]);

        var result = await service.DiscoverHostAsync("   ");

        Assert.False(result.IsSuccess);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("请输入", result.Message);
    }

    [Fact]
    public async Task DiscoverHostAsync_不可达地址_快速失败且不抛异常()
    {
        var service = new NetworkDiscoveryService([]);
        var stopwatch = Stopwatch.StartNew();

        var result = await service.DiscoverHostAsync("192.0.2.1", new DiscoveryOptions(TimeSpan.FromMilliseconds(600)));
        stopwatch.Stop();

        Assert.False(result.IsSuccess);
        Assert.Equal(OperationStatus.NeedsUserAction, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"耗时 {stopwatch.Elapsed}");
    }

    private sealed class ThrowingProvider : INetworkDiscovery
    {
        public string Name => "抛异常";

        public void Dispose()
        {
        }

        public Task<IReadOnlyList<DiscoveredPrinter>> DiscoverAsync(DiscoveryOptions options, CancellationToken ct) =>
            throw new InvalidOperationException("模拟发现器故障");
    }

    private static DiscoveredPrinter Printer(string address, string model, string vendor, string source) => new()
    {
        Id = DiscoveryText.BuildNetworkId(address),
        DisplayName = model,
        Model = model,
        Vendor = vendor,
        Address = address,
        DiscoverySource = source,
    };
}

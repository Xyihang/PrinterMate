using System.Net;
using PrinterMate.Core.Discovery;
using Xunit;

namespace PrinterMate.UnitTests.Discovery;

/// <summary>IppProbe 的 URI 构造与离线快速失败测试(不依赖真实网络与打印机)。</summary>
public sealed class IppProbeTests
{
    [Fact]
    public void BuildIppUri_默认端口与资源() =>
        Assert.Equal("ipp://192.168.1.20:631/ipp/print", IppProbe.BuildIppUri("192.168.1.20").ToString());

    [Fact]
    public void BuildIppUri_自定义端口与资源() =>
        Assert.Equal("ipp://printer.local:8631/ipp", IppProbe.BuildIppUri("printer.local", 8631, "ipp").ToString());

    [Fact]
    public void BuildIppUri_资源路径补前导斜杠() =>
        Assert.Equal("ipp://printer.local:631/ipp/print", IppProbe.BuildIppUri("printer.local", 631, "ipp/print").ToString());

    [Fact]
    public void BuildIppUri_方括号主机名被规范化() =>
        Assert.Equal("ipp://printer.local:631/ipp/print", IppProbe.BuildIppUri("[printer.local]").ToString());

    [Fact]
    public async Task ProbeAsync_不可达地址_返回null且不抛异常()
    {
        // 192.0.2.0/24 是 RFC 5737 保留测试网段,不会真的连上打印机。
        var probe = new IppProbe(TimeSpan.FromMilliseconds(300));
        var options = new DiscoveryOptions(TimeSpan.FromMilliseconds(600));

        var result = await probe.ProbeAsync(IPAddress.Parse("192.0.2.7"), options, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ProbeHostAsync_空主机名_返回null() =>
        Assert.Null(await new IppProbe().ProbeHostAsync("   ", DiscoveryOptions.Default, CancellationToken.None));

    [Fact]
    public async Task ProbeAsync_预先取消_返回null且不抛异常()
    {
        var probe = new IppProbe(TimeSpan.FromMilliseconds(200));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await probe.ProbeAsync(IPAddress.Parse("192.0.2.7"), new DiscoveryOptions(TimeSpan.FromMilliseconds(200)), cts.Token);

        Assert.Null(result);
    }

    [Fact]
    public void PrintingIppAttributeSource_复用打印层的IppClient且构造安全()
    {
        var source = new PrintingIppAttributeSource(TimeSpan.FromMilliseconds(100));

        Assert.NotNull(source);
        Assert.True(PrintingIppAttributeSource.IsAvailable);
    }

    [Fact]
    public async Task PrintingIppAttributeSource_不可达地址_返回null且不抛异常()
    {
        var source = new PrintingIppAttributeSource(TimeSpan.FromMilliseconds(200));

        var result = await source.GetAttributesAsync(IppProbe.BuildIppUri("192.0.2.7"), CancellationToken.None);

        Assert.Null(result);
    }
}

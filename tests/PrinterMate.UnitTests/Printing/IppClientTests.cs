using PrinterMate.Printing;
using Xunit;

namespace PrinterMate.UnitTests.Printing;

public class IppClientTests
{
    [Theory]
    [InlineData("192.168.1.50", null, "ipp", "192.168.1.50", 631, "/ipp/print")]
    [InlineData("printer.local", null, "ipp", "printer.local", 631, "/ipp/print")]
    [InlineData("192.168.1.50:9100", "/ipp/print", "ipp", "192.168.1.50", 9100, "/ipp/print")]
    [InlineData("ipp://192.168.1.50:631/ipp/print", null, "ipp", "192.168.1.50", 631, "/ipp/print")]
    [InlineData("ipp://192.168.1.50/ipp/print", null, "ipp", "192.168.1.50", 631, "/ipp/print")]
    [InlineData("http://192.168.1.50:8080/ipp/print", null, "ipp", "192.168.1.50", 8080, "/ipp/print")]
    [InlineData("https://printer.local/ipp/print", null, "ipps", "printer.local", 631, "/ipp/print")]
    [InlineData("ipps://printer.local:8631/ipp/print", null, "ipps", "printer.local", 8631, "/ipp/print")]
    public void BuildIppUri_parses_host_port_and_scheme(
        string host, string? resource, string scheme, string hostName, int port, string path)
    {
        var uri = resource is null
            ? IppClient.BuildIppUri(host)
            : IppClient.BuildIppUri(host, 631, resource);

        Assert.Equal(scheme, uri.Scheme);
        Assert.Equal(hostName, uri.Host);
        Assert.Equal(port, uri.Port);
        Assert.Equal(path, uri.AbsolutePath);
    }

    [Fact]
    public void BuildIppUri_uses_explicit_port_when_host_has_none()
    {
        var uri = IppClient.BuildIppUri("192.168.1.50", 8631);
        Assert.Equal(8631, uri.Port);
        Assert.Equal("192.168.1.50", uri.Host);
    }

    [Fact]
    public void BuildIppUri_keeps_explicit_port_over_default_parameter()
    {
        var uri = IppClient.BuildIppUri("192.168.1.50:9100", 631, "/ipp/print");
        Assert.Equal(9100, uri.Port);
    }

    [Theory]
    [InlineData("ipp/print", "/ipp/print")]
    [InlineData("/ipp/print", "/ipp/print")]
    [InlineData("/printers/office", "/printers/office")]
    [InlineData("", "/ipp/print")]
    [InlineData("   ", "/ipp/print")]
    public void BuildIppUri_normalizes_resource(string resource, string expected)
    {
        var uri = IppClient.BuildIppUri("192.168.1.50", 631, resource);
        Assert.Equal(expected, uri.AbsolutePath);
    }

    [Fact]
    public void BuildIppUri_builds_the_documented_default_value()
    {
        var uri = IppClient.BuildIppUri("192.168.1.50");
        Assert.Equal("ipp://192.168.1.50:631/ipp/print", uri.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("://")]
    [InlineData("ipp://")]
    public void BuildIppUri_rejects_empty_or_invalid_host(string host)
    {
        Assert.Throws<ArgumentException>(() => IppClient.BuildIppUri(host));
    }

    [Fact]
    public void BuildIppUri_rejects_null_host()
    {
        Assert.Throws<ArgumentException>(() => IppClient.BuildIppUri(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void BuildIppUri_rejects_out_of_range_port(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IppClient.BuildIppUri("192.168.1.50", port));
    }

    [Fact]
    public void Constructor_accepts_default_and_custom_timeout()
    {
        _ = new IppClient();
        _ = new IppClient(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_rejects_non_positive_timeout(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IppClient(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task ValidateJobAsync_rejects_empty_document_format()
    {
        var client = new IppClient(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ValidateJobAsync(new Uri("ipp://127.0.0.1:631/ipp/print"), "  "));
    }

    [Fact]
    public async Task PrintBytesAsync_rejects_empty_payload()
    {
        var client = new IppClient(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.PrintBytesAsync(new Uri("ipp://127.0.0.1:631/ipp/print"), Array.Empty<byte>(), "doc", "application/pdf"));
    }

    [Fact(Skip = "需要真实 IPP 打印机,单元测试环境不联网")]
    public async Task GetAttributesAsync_reads_a_real_device()
    {
        var client = new IppClient();
        var info = await client.GetAttributesAsync(new Uri("ipp://192.168.1.50:631/ipp/print"));
        Assert.NotNull(info);
    }
}

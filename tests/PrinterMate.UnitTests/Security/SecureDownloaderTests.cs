using PrinterMate.Security;
using Xunit;

namespace PrinterMate.UnitTests.Security;

public class SecureDownloaderTests : IDisposable
{
    private readonly string _directory;

    public SecureDownloaderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pm-sec-download", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    private string Destination => Path.Combine(_directory, "payload.exe");

    [Fact]
    public async Task DownloadAsync_HttpUrl_ThrowsPolicyExceptionWithoutNetworkRequest()
    {
        using var downloader = new SecureDownloader();

        var ex = await Assert.ThrowsAsync<SecurityPolicyException>(
            () => downloader.DownloadAsync("http://ftp.hp.com/pub/driver.exe", Destination));

        Assert.Contains("https", ex.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(ex.Suggestion);
        AssertNoDownloadArtifacts();
    }

    [Fact]
    public async Task DownloadAsync_FtpUrl_ThrowsPolicyException()
    {
        using var downloader = new SecureDownloader();

        var ex = await Assert.ThrowsAsync<SecurityPolicyException>(
            () => downloader.DownloadAsync("ftp://ftp.hp.com/pub/driver.exe", Destination));

        Assert.Contains("https", ex.Reason, StringComparison.OrdinalIgnoreCase);
        AssertNoDownloadArtifacts();
    }

    [Fact]
    public async Task DownloadAsync_FileUrl_ThrowsPolicyException()
    {
        using var downloader = new SecureDownloader();

        await Assert.ThrowsAsync<SecurityPolicyException>(
            () => downloader.DownloadAsync("file:///C:/windows/system32/driver.cab", Destination));

        AssertNoDownloadArtifacts();
    }

    [Fact]
    public async Task DownloadAsync_NonAllowlistedHttpsHost_ThrowsPolicyException()
    {
        using var downloader = new SecureDownloader();

        var ex = await Assert.ThrowsAsync<SecurityPolicyException>(
            () => downloader.DownloadAsync("https://driverscollection.com/hp.exe", Destination));

        Assert.Contains("白名单", ex.Reason);
        AssertNoDownloadArtifacts();
    }

    [Fact]
    public async Task DownloadAsync_MalformedUrl_ThrowsPolicyException()
    {
        using var downloader = new SecureDownloader();

        await Assert.ThrowsAsync<SecurityPolicyException>(
            () => downloader.DownloadAsync("这不是一个网址", Destination));

        AssertNoDownloadArtifacts();
    }

    private void AssertNoDownloadArtifacts()
    {
        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(Destination + ".part"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

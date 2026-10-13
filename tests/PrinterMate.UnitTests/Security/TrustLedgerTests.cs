using PrinterMate.Security;
using Xunit;

namespace PrinterMate.UnitTests.Security;

public class TrustLedgerTests : IDisposable
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    private readonly string _path;

    public TrustLedgerTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "pm-sec-ledger", Guid.NewGuid().ToString("N"), "trust-ledger.json");
    }

    [Fact]
    public void VerifyOrPin_FirstTimePinsAndSucceeds()
    {
        var ledger = new TrustLedger(_path);

        var ok = ledger.VerifyOrPin("url:https://ftp.hp.com/a.exe", HashA, "CN=HP Inc.", "https://ftp.hp.com/a.exe", out var reason);

        Assert.True(ok);
        Assert.NotEmpty(reason);
        Assert.True(ledger.TryGet("url:https://ftp.hp.com/a.exe", out var entry));
        Assert.Equal(HashA, entry.Sha256);
        Assert.Equal("CN=HP Inc.", entry.SignerSubject);
    }

    [Fact]
    public void VerifyOrPin_SameHashSucceeds()
    {
        var ledger = new TrustLedger(_path);
        ledger.VerifyOrPin("k", HashA, "sub", "src", out _);

        var ok = ledger.VerifyOrPin("k", HashA.ToUpperInvariant(), "sub", "src", out var reason);

        Assert.True(ok);
        Assert.Contains("一致", reason);
    }

    [Fact]
    public void VerifyOrPin_DifferentHashIsRejected()
    {
        var ledger = new TrustLedger(_path);
        ledger.VerifyOrPin("k", HashA, "sub", "src", out _);

        var ok = ledger.VerifyOrPin("k", HashB, "sub", "src", out var reason);

        Assert.False(ok);
        Assert.NotEmpty(reason);
        Assert.Contains("不一致", reason);
    }

    [Fact]
    public void VerifyOrPin_PersistsAcrossInstances()
    {
        var first = new TrustLedger(_path);
        first.VerifyOrPin("k", HashA, "sub", "src", out _);

        var second = new TrustLedger(_path);

        Assert.True(second.TryGet("k", out var entry));
        Assert.Equal(HashA, entry.Sha256);
        Assert.False(second.VerifyOrPin("k", HashB, "sub", "src", out _));
    }

    [Fact]
    public void VerifyOrPin_RejectsInvalidHash()
    {
        var ledger = new TrustLedger(_path);

        Assert.False(ledger.VerifyOrPin("k", "not-a-hash", "sub", "src", out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Pin_OverwritesExistingEntry()
    {
        var ledger = new TrustLedger(_path);
        ledger.Pin(new TrustLedgerEntry("k", HashA, "s1", DateTimeOffset.Now, "u1"));
        ledger.Pin(new TrustLedgerEntry("k", HashB, "s2", DateTimeOffset.Now, "u2"));

        Assert.True(ledger.TryGet("k", out var entry));
        Assert.Equal(HashB, entry.Sha256);
        Assert.Equal("s2", entry.SignerSubject);
    }

    public void Dispose()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

using PrinterMate.Security;
using Xunit;

namespace PrinterMate.UnitTests.Security;

public class Sha256VerifierTests
{
    // SHA256("hello") 的已知值。
    private const string HelloHash = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

    [Fact]
    public void ComputeHex_ReturnsExpectedKnownHash()
    {
        var path = TempFile.Write("hello");

        try
        {
            Assert.Equal(HelloHash, Sha256Verifier.ComputeHex(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ComputeHexAsync_ReturnsExpectedKnownHash()
    {
        var path = TempFile.Write("hello");

        try
        {
            Assert.Equal(HelloHash, await Sha256Verifier.ComputeHexAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Equals_IsCaseInsensitiveAndIgnoresWhitespace()
    {
        Assert.True(Sha256Verifier.Equals(HelloHash, HelloHash.ToUpperInvariant()));
        Assert.True(Sha256Verifier.Equals($"  {HelloHash}  ", HelloHash));
        Assert.True(Sha256Verifier.Equals(HelloHash.ToUpperInvariant(), $" {HelloHash.ToUpperInvariant()} "));
    }

    [Fact]
    public void Equals_RejectsDifferentOrInvalidValues()
    {
        Assert.False(Sha256Verifier.Equals(HelloHash, new string('0', 64)));
        Assert.False(Sha256Verifier.Equals(HelloHash, "abc"));
        Assert.False(Sha256Verifier.Equals(null, HelloHash));
        Assert.False(Sha256Verifier.Equals(HelloHash, null));
        Assert.False(Sha256Verifier.Equals(new string('z', 64), HelloHash));
    }

    [Fact]
    public void IsValidHex_AcceptsOnlySixtyFourHexChars()
    {
        Assert.True(Sha256Verifier.IsValidHex(HelloHash));
        Assert.True(Sha256Verifier.IsValidHex(HelloHash.ToUpperInvariant()));
        Assert.True(Sha256Verifier.IsValidHex($" {HelloHash} "));

        Assert.False(Sha256Verifier.IsValidHex(null));
        Assert.False(Sha256Verifier.IsValidHex(string.Empty));
        Assert.False(Sha256Verifier.IsValidHex(HelloHash[..63]));
        Assert.False(Sha256Verifier.IsValidHex(HelloHash + "a"));
        Assert.False(Sha256Verifier.IsValidHex(new string('z', 64)));
    }

    [Fact]
    public void ComputeHex_ThrowsForEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => Sha256Verifier.ComputeHex(" "));
    }

    private static class TempFile
    {
        public static string Write(string content)
        {
            var path = Path.Combine(Path.GetTempPath(), $"pm-sec-{Guid.NewGuid():N}.bin");
            File.WriteAllText(path, content);
            return path;
        }
    }
}

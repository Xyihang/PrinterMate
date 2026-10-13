using System.Security.Cryptography;

namespace PrinterMate.Security;

/// <summary>SHA256 十六进制校验工具。</summary>
public static class Sha256Verifier
{
    private const int HexLength = 64;

    /// <summary>计算文件的 SHA256(小写十六进制)。</summary>
    public static string ComputeHex(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("文件路径为空,无法计算 SHA256。", nameof(filePath));
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ToHex(SHA256.HashData(stream));
    }

    /// <summary>异步计算文件的 SHA256(小写十六进制)。</summary>
    public static async Task<string> ComputeHexAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("文件路径为空,无法计算 SHA256。", nameof(filePath));
        }

        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return ToHex(hash);
    }

    /// <summary>大小写无关、忽略空白的哈希比较;任一参数不是 64 位十六进制则返回 false。</summary>
    public static bool Equals(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断是否为合法的 64 位十六进制哈希(忽略空白)。</summary>
    public static bool IsValidHex(string? hash)
    {
        if (hash is null)
        {
            return false;
        }

        var normalized = Normalize(hash);
        if (normalized is null || normalized.Length != HexLength)
        {
            return false;
        }

        foreach (var c in normalized)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var buffer = new char[value.Length];
        var count = 0;
        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c))
            {
                buffer[count++] = c;
            }
        }

        return count == HexLength ? new string(buffer, 0, count) : null;
    }

    private static string ToHex(byte[] hash)
        => Convert.ToHexString(hash).ToLowerInvariant();
}

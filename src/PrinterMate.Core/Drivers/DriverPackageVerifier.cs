using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PrinterMate.Interop;

namespace PrinterMate.Core.Drivers;

/// <summary>驱动包校验结果。</summary>
public sealed record PackageVerification(
    bool Passed,
    string Message,
    string Suggestion,
    string InfPath,
    string Sha256,
    string CatalogPath,
    string SignerSubject,
    string Thumbprint)
{
    public static PackageVerification Fail(string message, string suggestion, string infPath) =>
        new(false, message, suggestion, infPath, string.Empty, string.Empty, string.Empty, string.Empty);
}

/// <summary>
/// 驱动包校验器。
///
/// 关键事实:厂商驱动包的 Authenticode 签名在 **.cat 目录文件**上,而不是 .inf 上;
/// 直接对 .inf 调用 WinVerifyTrust 会返回 TRUST_E_NOSIGNATURE。
/// 因此校验顺序是:
///   1) 对 .inf 计算 SHA256 并与清单比对(防篡改);
///   2) 对同目录下的 .cat 调用 WinVerifyTrust 校验签名与证书链;
///   3) 若包内没有 .cat,再退回校验 .inf 自身的嵌入签名;
///   4) 两者都不存在 → 判定为无法验证,拒绝安装。
/// </summary>
public static class DriverPackageVerifier
{
    private static readonly Regex CatalogDirective = new(
        @"^\s*CatalogFile(\.[A-Za-z0-9_]+)?\s*=\s*(?<name>[^\r\n;]+)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ComputeInfSha256(string infPath)
    {
        using var stream = File.OpenRead(infPath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>在 INF 同目录下寻找目录文件(.cat)。优先使用 INF 中 CatalogFile= 指令指定的文件。</summary>
    public static string? FindCatalog(string infPath)
    {
        var directory = Path.GetDirectoryName(infPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            var infText = File.ReadAllText(infPath);
            foreach (Match match in CatalogDirective.Matches(infText))
            {
                var name = match.Groups["name"].Value.Trim().Trim('"');
                if (name.Length == 0)
                {
                    continue;
                }

                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception)
        {
            // INF 读取失败时退回目录扫描
        }

        return Directory.EnumerateFiles(directory, "*.cat")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>完整校验驱动包。</summary>
    public static PackageVerification Verify(
        string infPath,
        string? expectedSha256 = null,
        string? expectedSignerSubject = null,
        string? expectedBrand = null)
    {
        if (string.IsNullOrWhiteSpace(infPath) || !File.Exists(infPath))
        {
            return PackageVerification.Fail($"驱动 INF 文件不存在:{infPath}", "请重新导入或下载该驱动包。", infPath);
        }

        string sha;
        try
        {
            sha = ComputeInfSha256(infPath);
        }
        catch (Exception ex)
        {
            return PackageVerification.Fail($"计算驱动包 SHA256 失败:{ex.Message}", "请确认文件未被占用且有读取权限。", infPath);
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256)
            && !string.Equals(sha, expectedSha256.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase))
        {
            return PackageVerification.Fail(
                $"驱动包 SHA256 与清单不一致,已拒绝安装(实际 {sha},期望 {expectedSha256})。",
                "驱动文件可能被修改或替换。请删除该驱动并从官方源重新获取。",
                infPath) with { Sha256 = sha };
        }

        var catalog = FindCatalog(infPath);
        var target = catalog ?? infPath;
        var signature = AuthenticodeVerifier.Verify(target);

        if (!signature.IsTrusted)
        {
            if (catalog is null && !signature.HasSignature)
            {
                return PackageVerification.Fail(
                    "驱动包内既没有目录文件(.cat),INF 本身也没有数字签名,无法验证来源,已拒绝安装。",
                    "请使用厂商提供的完整驱动包(应包含 .inf 与 .cat)。",
                    infPath) with { Sha256 = sha };
            }

            return PackageVerification.Fail(
                $"驱动包数字签名校验未通过:{signature.Message}",
                signature.Status == 0x800B0111
                    ? "该证书被系统标记为不信任,请勿安装此驱动。"
                    : "请确认驱动来自厂商官方源,或改用免驱通道。",
                infPath) with { Sha256 = sha, CatalogPath = target };
        }

        if (!string.IsNullOrWhiteSpace(expectedSignerSubject))
        {
            var expected = Normalize(expectedSignerSubject);
            var actual = Normalize(signature.SignerSubject);
            if (actual.Length > 0 && !actual.Contains(expected, StringComparison.OrdinalIgnoreCase)
                && !expected.Contains(actual, StringComparison.OrdinalIgnoreCase))
            {
                return PackageVerification.Fail(
                    $"驱动包签名主体与清单不一致,已拒绝安装(实际:{signature.SignerSubject})。",
                    "请确认该驱动确实来自期望的厂商;若为贴牌驱动,请在驱动清单中更新签名主体。",
                    infPath) with { Sha256 = sha, CatalogPath = target, SignerSubject = signature.SignerSubject };
            }
        }

        var subject = signature.SignerSubject;
        var brandNote = string.IsNullOrWhiteSpace(subject) ? string.Empty : $",签名主体:{subject}";
        var fileNote = catalog is null ? "INF 嵌入签名" : $"目录文件 {Path.GetFileName(catalog)}";

        return new PackageVerification(
            true,
            $"驱动包校验通过({fileNote},签名有效{brandNote})。",
            string.Empty,
            infPath,
            sha,
            target,
            subject,
            signature.SignerThumbprint);
    }

    private static string Normalize(string subject) => subject.Replace(" ", string.Empty).Trim();
}

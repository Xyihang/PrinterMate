using PrinterMate.Interop;

namespace PrinterMate.Security;

/// <summary>签名策略校验结果。</summary>
public sealed record SignaturePolicyResult(
    bool Passed,
    string SignerSubject,
    string Thumbprint,
    string Reason,
    string? Suggestion);

/// <summary>
/// Authenticode 签名策略:在 WinVerifyTrust 校验通过的基础上,进一步要求签名主体
/// 匹配期望主体,或属于白名单中的厂商名称/官方域名。
/// </summary>
public sealed class SignaturePolicy
{
    private const string DefaultSuggestion =
        "请从厂商官方网站重新下载安装包,勿使用来源不明的驱动文件。";

    /// <summary>
    /// 校验文件签名。
    /// <paramref name="expectedSignerSubject"/> 为空时,只要求签名有效且主体属于白名单厂商域名/名称。
    /// </summary>
    public SignaturePolicyResult Verify(
        string filePath,
        string? expectedSignerSubject = null,
        string? expectedBrand = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new SignaturePolicyResult(
                false, string.Empty, string.Empty, "文件路径为空,无法校验数字签名。", DefaultSuggestion);
        }

        if (!File.Exists(filePath))
        {
            return new SignaturePolicyResult(
                false, string.Empty, string.Empty, $"文件不存在,无法校验数字签名:{filePath}", DefaultSuggestion);
        }

        SignatureCheckResult result;
        try
        {
            result = AuthenticodeVerifier.Verify(filePath);
        }
        catch (Exception ex)
        {
            return new SignaturePolicyResult(
                false, string.Empty, string.Empty, $"数字签名校验过程出错:{ex.Message}", DefaultSuggestion);
        }

        if (!result.HasSignature)
        {
            return new SignaturePolicyResult(
                false, string.Empty, string.Empty, "文件没有嵌入式数字签名,无法证明来源,已拒绝。", DefaultSuggestion);
        }

        if (!result.IsTrusted)
        {
            var reason = string.IsNullOrWhiteSpace(result.Message)
                ? AuthenticodeVerifier.DescribeStatus(result.Status)
                : result.Message;
            return new SignaturePolicyResult(
                false, result.SignerSubject, result.SignerThumbprint,
                $"数字签名校验未通过:{reason}", DefaultSuggestion);
        }

        if (!string.IsNullOrWhiteSpace(expectedSignerSubject))
        {
            if (!SubjectContains(result.SignerSubject, expectedSignerSubject))
            {
                return new SignaturePolicyResult(
                    false, result.SignerSubject, result.SignerThumbprint,
                    $"签名主体与期望不一致:期望包含「{expectedSignerSubject.Trim()}」,实际为「{result.SignerSubject}」,已拒绝。",
                    "请确认下载的是该厂商发布的正确安装包。");
            }

            return new SignaturePolicyResult(
                true, result.SignerSubject, result.SignerThumbprint, "数字签名有效且主体符合期望。", null);
        }

        var allowlist = DownloadSourceAllowlist.Default;
        if (!string.IsNullOrWhiteSpace(expectedBrand))
        {
            if (SubjectMatchesBrand(result, expectedBrand.Trim(), allowlist))
            {
                return new SignaturePolicyResult(
                    true, result.SignerSubject, result.SignerThumbprint,
                    $"数字签名有效且主体属于品牌「{expectedBrand.Trim()}」。", null);
            }

            return new SignaturePolicyResult(
                false, result.SignerSubject, result.SignerThumbprint,
                $"签名主体「{result.SignerSubject}」不属于期望品牌「{expectedBrand.Trim()}」,已拒绝。",
                $"请从「{expectedBrand.Trim()}」官方渠道下载安装包。");
        }

        // 微软作为 Windows Update 的再分发签名方(MOPR = Microsoft OEM Preinstallation):
        // 它替各厂商签名并分发官方驱动包,签名主体是 Microsoft 而不是 HP/Canon。
        // 这是 Windows Update 的标准形态,不是"来源不明的驱动",必须承认;
        // 该分支只在【没有指定期望签名主体、也没有指定期望品牌】的通用信任路径下生效,
        // 因此不会削弱"厂商域名要求三方一致"的规则。
        if (IsMicrosoftRedistributorSignature(result.SignerSubject))
        {
            return new SignaturePolicyResult(
                true, result.SignerSubject, result.SignerThumbprint,
                "数字签名有效,且为微软官方再分发签名(Windows Update / MOPR)。", null);
        }

        if (MatchesAnyWhitelistedVendor(result, allowlist, out var matchedBrand))
        {
            return new SignaturePolicyResult(
                true, result.SignerSubject, result.SignerThumbprint,
                $"数字签名有效,签名主体属于白名单厂商「{matchedBrand}」。", null);
        }

        return new SignaturePolicyResult(
            false, result.SignerSubject, result.SignerThumbprint,
            $"数字签名虽然有效,但签名主体「{result.SignerSubject}」不属于任何白名单厂商,已拒绝。",
            DefaultSuggestion);
    }

    /// <summary>
    /// 判断是否为微软再分发签名(Windows Update / MOPR)。
    /// 微软替厂商分发官方驱动包时会加自己的签名,主体中出现 Microsoft Corporation。
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static bool IsMicrosoftRedistributorSignature(string? subject)
        => !string.IsNullOrWhiteSpace(subject)
           && subject.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase);

    private static bool SubjectMatchesBrand(SignatureCheckResult result, string brand, DownloadSourceAllowlist allowlist)
    {
        if (SubjectContains(result.SignerSubject, brand))
        {
            return true;
        }

        foreach (var vendor in allowlist.VendorDomains)
        {
            if (!vendor.Brand.Equals(brand, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SubjectContains(result.SignerSubject, vendor.Domain)
                || SubjectContains(result.SignerSubject, vendor.Domain.Split('.')[0]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAnyWhitelistedVendor(
        SignatureCheckResult result, DownloadSourceAllowlist allowlist, out string matchedBrand)
    {
        foreach (var vendor in allowlist.VendorDomains)
        {
            if (SubjectContains(result.SignerSubject, vendor.Brand)
                || SubjectContains(result.SignerSubject, vendor.Domain))
            {
                matchedBrand = vendor.Brand;
                return true;
            }
        }

        matchedBrand = string.Empty;
        return false;
    }

    private static bool SubjectContains(string subject, string token)
    {
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return subject.Contains(token.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

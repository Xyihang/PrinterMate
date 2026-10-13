using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace PrinterMate.Interop;

/// <summary>Authenticode 校验结果。</summary>
public sealed record SignatureCheckResult(
    bool IsTrusted,
    bool HasSignature,
    string SignerSubject,
    string SignerThumbprint,
    uint Status,
    string Message)
{
    public static readonly SignatureCheckResult Missing = new(false, false, string.Empty, string.Empty, 0x800B0100, "文件没有嵌入式数字签名。");
}

/// <summary>
/// 基于 WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2)的 Authenticode 签名校验:
/// 校验整条证书链、吊销状态与签名完整性,并在通过后提取签名主体名称。
/// </summary>
public static class AuthenticodeVerifier
{
    private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
    private const uint TRUST_E_BAD_DIGEST = 0x80096010;
    private const uint CERT_E_REVOKED = 0x800B010C;
    private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;
    private const uint CERT_E_EXPIRED = 0x800B0101;
    private const uint TRUST_E_EXPLICIT_DISTRUST = 0x800B0111;

    public static SignatureCheckResult Verify(string filePath, bool checkRevocation = true)
    {
        if (!File.Exists(filePath))
        {
            return new SignatureCheckResult(false, false, string.Empty, string.Empty, 2, $"文件不存在:{filePath}");
        }

        var fileInfo = WinTrustNative.WINTRUST_FILE_INFO.Create(filePath);
        var trustData = WinTrustNative.WINTRUST_DATA.Create(IntPtr.Zero, 0);
        var trustDataPtr = IntPtr.Zero;
        var fileInfoPtr = IntPtr.Zero;

        try
        {
            var flags = WinTrustNative.WTD_DISABLE_MD2_MD4;
            flags |= checkRevocation
                ? WinTrustNative.WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
                : WinTrustNative.WTD_CACHE_ONLY_URL_RETRIEVAL;

            trustData.dwProvFlags = flags;

            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustNative.WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            trustData.pFile = fileInfoPtr;
            trustDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustNative.WINTRUST_DATA>());
            Marshal.StructureToPtr(trustData, trustDataPtr, false);

            var action = WinTrustNative.WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var status = WinTrustNative.WinVerifyTrust(IntPtr.Zero, ref action, trustDataPtr);

            // 无论结果如何都要关闭状态句柄,否则会泄漏 StateData。
            trustData.dwStateAction = WinTrustNative.WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(trustData, trustDataPtr, false);
            WinTrustNative.WinVerifyTrust(IntPtr.Zero, ref action, trustDataPtr);

            if (status == WinTrustNative.S_OK)
            {
                var (subject, thumbprint) = TryReadSigner(filePath);
                return new SignatureCheckResult(true, true, subject, thumbprint, status, "数字签名有效,证书链受信任。");
            }

            var hasSignature = status != TRUST_E_NOSIGNATURE;
            var (subj2, thumb2) = hasSignature ? TryReadSigner(filePath) : (string.Empty, string.Empty);
            return new SignatureCheckResult(false, hasSignature, subj2, thumb2, status, DescribeStatus(status));
        }
        finally
        {
            if (trustDataPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(trustDataPtr);
            }

            if (fileInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPtr);
            }

            if (fileInfo.pcwszFilePath != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            }
        }
    }

    private static (string Subject, string Thumbprint) TryReadSigner(string filePath)
    {
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
            using var cert2 = new X509Certificate2(cert);
            return (cert2.Subject, cert2.Thumbprint);
        }
        catch (Exception)
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>把常见的 WinVerifyTrust 状态码翻译成中文说明。</summary>
    public static string DescribeStatus(uint status) => status switch
    {
        TRUST_E_NOSIGNATURE => "文件未签名,或签名无法被系统识别。",
        TRUST_E_BAD_DIGEST => "签名摘要不匹配:文件在签名后被修改过。",
        CERT_E_UNTRUSTEDROOT => "签名证书链的根证书不受信任。",
        CERT_E_EXPIRED => "签名证书已过期。",
        CERT_E_REVOKED => "签名证书已被吊销。",
        TRUST_E_EXPLICIT_DISTRUST => "该证书被系统显式标记为不信任。",
        0x800B010A => "证书链无法构建:缺少中间证书。",
        0x800B0110 => "证书用途与该文件类型不匹配。",
        _ => $"签名校验未通过(HRESULT 0x{status:X8})。",
    };
}

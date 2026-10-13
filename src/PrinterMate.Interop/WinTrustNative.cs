using System.Runtime.InteropServices;

namespace PrinterMate.Interop;

/// <summary>
/// WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2)Authenticode 校验所需的原始声明。
/// 结构布局在 x64/ARM64 下均为 8 字节对齐,字段顺序与 wintrust.h 一致。
/// </summary>
internal static class WinTrustNative
{
    internal const string Wintrust = "wintrust.dll";

    internal const uint WTD_UI_NONE = 2;
    internal const uint WTD_REVOKE_NONE = 0;
    internal const uint WTD_REVOKE_WHOLECHAIN = 1;
    internal const uint WTD_CHOICE_FILE = 1;
    internal const uint WTD_STATEACTION_VERIFY = 1;
    internal const uint WTD_STATEACTION_CLOSE = 2;
    internal const uint WTD_SAFER_FLAG = 0x00000100;
    internal const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;
    internal const uint WTD_REVOCATION_CHECK_END_CERT = 0x00000010;
    internal const uint WTD_REVOCATION_CHECK_CHAIN = 0x00000020;
    internal const uint WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT = 0x00000080;
    internal const uint WTD_DISABLE_MD2_MD4 = 0x00002000;

    /// <summary>0x00000000 表示签名有效(TRUST_E_NOSIGNATURE 等其它值均为失败)。</summary>
    internal const uint S_OK = 0x00000000;

    internal static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;

        internal static WINTRUST_FILE_INFO Create(string path)
        {
            return new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = Marshal.StringToCoTaskMemUni(path),
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;

        internal static WINTRUST_DATA Create(IntPtr pFileInfo, uint provFlags)
        {
            return new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFileInfo,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = provFlags,
            };
        }
    }

    [DllImport(Wintrust, EntryPoint = "WinVerifyTrust", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);
}

using Microsoft.Win32.SafeHandles;

namespace PrinterMate.Interop;

/// <summary>包装打印机句柄,确保 ClosePrinter 一定被调用。</summary>
public sealed class SafePrinterHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafePrinterHandle() : base(true)
    {
    }

    internal SafePrinterHandle(IntPtr existing, bool ownsHandle) : base(ownsHandle)
    {
        SetHandle(existing);
    }

    protected override bool ReleaseHandle() => SpoolNative.ClosePrinter(handle);
}

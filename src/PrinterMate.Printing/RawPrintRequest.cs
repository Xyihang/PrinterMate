namespace PrinterMate.Printing;

/// <summary>提权辅助进程与主程序之间的 RAW 打印请求契约。</summary>
public sealed record RawPrintRequest(string PrinterName, string FilePath, string DocumentName);

using System.IO;

namespace PrinterMate.Printing;

/// <summary>面向用户的参数校验,统一抛中文异常并给出可操作建议。</summary>
internal static class PrintingErrors
{
    public static void RequirePrinterName(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            throw new ArgumentException("打印机名称为空。请先选择一台打印机,再执行打印操作。", nameof(printerName));
        }
    }

    public static FileInfo RequireFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("文件路径为空。请选择要打印的文件后重试。", nameof(filePath));
        }

        var file = new FileInfo(filePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                $"找不到文件「{filePath}」。请确认文件未被移动、重命名或删除,或重新选择文件。",
                filePath);
        }

        return file;
    }
}

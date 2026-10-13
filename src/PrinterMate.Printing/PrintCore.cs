using System.Printing;
using System.Windows.Documents;
using System.Windows.Xps;

namespace PrinterMate.Printing;

/// <summary>把 FixedDocument / XPS 内容投递到本机打印队列的公共出口。</summary>
internal static class PrintCore
{
    public static PrintQueue ResolveQueue(string printerName)
    {
        try
        {
            var server = new LocalPrintServer();
            return server.GetPrintQueue(printerName);
        }
        catch (PrintQueueException ex)
        {
            throw new InvalidOperationException(
                $"找不到打印机「{printerName}」或无法打开其打印队列:{ex.Message}。请确认设备已安装、已开机,并在「设备和打印机」中可见。",
                ex);
        }
        catch (PrintServerException ex)
        {
            throw new InvalidOperationException(
                $"后台打印服务不可用:{ex.Message}。请在「服务」中启动 Print Spooler(打印后台处理程序)后重试。",
                ex);
        }
    }

    public static void Write(string printerName, DocumentPaginator paginator) =>
        WithWriter(printerName, writer => writer.Write(paginator));

    public static void Write(string printerName, FixedDocument document) =>
        WithWriter(printerName, writer => writer.Write(document));

    public static void Write(string printerName, FixedDocumentSequence sequence) =>
        WithWriter(printerName, writer => writer.Write(sequence));

    private static void WithWriter(string printerName, Action<XpsDocumentWriter> action)
    {
        var queue = ResolveQueue(printerName);
        try
        {
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            action(writer);
        }
        catch (PrintQueueException ex)
        {
            throw new InvalidOperationException(
                $"向「{printerName}」提交打印任务失败:{ex.Message}。请确认打印机已开机、驱动安装完整,且队列未被暂停。",
                ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"向「{printerName}」提交打印任务失败:{ex.Message}。若设备较旧,请改用「系统测试页」或 RAW 方式打印。",
                ex);
        }
    }
}

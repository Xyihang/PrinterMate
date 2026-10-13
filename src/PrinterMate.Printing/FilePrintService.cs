using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using PrinterMate.Interop;

namespace PrinterMate.Printing;

/// <summary>可打印文件的类型。</summary>
public enum FilePrintKind
{
    Text,
    Image,
    Xps,
    Pdf,
    Raw,
    Unsupported,
}

/// <summary>按文件类型选择打印通道:文本/图片/XPS 自绘经 XPS 通道,RAW 直接进队列,PDF 交给系统关联程序。</summary>
public static class FilePrintService
{
    private const double TextMargin = 56;
    private const int ShellTimeoutMilliseconds = 60_000;

    private static readonly FontFamily DefaultFont =
        new("Microsoft YaHei UI, Microsoft YaHei, Segoe UI, Arial");

    public static FilePrintKind DetectKind(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return FilePrintKind.Unsupported;
        }

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension))
        {
            return FilePrintKind.Unsupported;
        }

        return extension.ToLowerInvariant() switch
        {
            ".txt" or ".log" or ".csv" or ".md" or ".json" or ".xml" or ".ini" or ".yaml" or ".yml"
                or ".htm" or ".html" or ".bat" or ".cmd" or ".ps1" => FilePrintKind.Text,
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" => FilePrintKind.Image,
            ".xps" or ".oxps" => FilePrintKind.Xps,
            ".pdf" => FilePrintKind.Pdf,
            ".bin" or ".prn" or ".ps" or ".pcl" or ".zpl" or ".epl" or ".spl" or ".raw" => FilePrintKind.Raw,
            _ => FilePrintKind.Unsupported,
        };
    }

    public static bool IsSupported(string filePath) => DetectKind(filePath) != FilePrintKind.Unsupported;

    /// <summary>用系统默认字体/编码把文本文件排版成 A4 多页文档后经 XPS 通道打印。</summary>
    public static void PrintText(string printerName, string filePath, Encoding? encoding = null, string? fontFamily = null)
    {
        PrintingErrors.RequirePrinterName(printerName);
        var file = PrintingErrors.RequireFile(filePath);

        var text = ReadAllText(file.FullName, encoding);
        if (text.Length == 0)
        {
            throw new InvalidOperationException(
                $"文件「{file.Name}」内容为空,没有可打印的内容。请确认文件已正确保存,或改用其它编码后重试。");
        }

        PrintCore.Write(printerName, BuildTextDocument(file.Name, text, fontFamily));
    }

    /// <summary>把图片等比缩放铺满 A4 可打印区域后经 XPS 通道打印。</summary>
    public static void PrintImage(string printerName, string filePath)
    {
        PrintingErrors.RequirePrinterName(printerName);
        var file = PrintingErrors.RequireFile(filePath);

        BitmapImage bitmap;
        try
        {
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.UriSource = new Uri(file.FullName, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法解析图片「{file.Name}」:{ex.Message}。请确认文件未损坏,或先转换为 PNG/JPEG 后重试。", ex);
        }

        PrintCore.Write(printerName, BuildImageDocument(bitmap));
    }

    /// <summary>把 XPS/OXPS 文档原样投递到打印队列。</summary>
    public static void PrintXps(string printerName, string filePath)
    {
        PrintingErrors.RequirePrinterName(printerName);
        var file = PrintingErrors.RequireFile(filePath);

        try
        {
            using var document = new XpsDocument(file.FullName, FileAccess.Read);
            var sequence = document.GetFixedDocumentSequence();
            if (sequence is null)
            {
                throw new InvalidOperationException(
                    $"XPS 文件「{file.Name}」中没有可打印的页面。请确认文件完整或重新导出后再试。");
            }

            PrintCore.Write(printerName, sequence);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"打开 XPS 文件「{file.Name}」失败:{ex.Message}。请确认文件未损坏,且系统已安装「Microsoft XPS 文档写入器」组件。", ex);
        }
    }

    /// <summary>把文件字节以 RAW 数据类型直接写入打印队列,适用于 ZPL/PCL/PS/PRN 等打印机原生指令。</summary>
    public static void PrintRaw(string printerName, string filePath)
    {
        PrintingErrors.RequirePrinterName(printerName);
        var file = PrintingErrors.RequireFile(filePath);

        var data = File.ReadAllBytes(file.FullName);
        if (data.Length == 0)
        {
            throw new InvalidOperationException(
                $"文件「{file.Name}」内容为空,没有可发送的打印数据。请确认导出时未中断。");
        }

        SpoolerApi.WriteRaw(printerName, data, file.Name);
    }

    /// <summary>PDF 走系统默认关联程序(ShellExecuteEx "print" 动词),带超时与失败提示。</summary>
    public static void PrintWithShell(string filePath, string? printerName = null)
    {
        var file = PrintingErrors.RequireFile(filePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = file.FullName,
            UseShellExecute = true,
        };

        if (string.IsNullOrWhiteSpace(printerName))
        {
            startInfo.Verb = "print";
        }
        else
        {
            startInfo.Verb = "printto";
            startInfo.Arguments = $"\"{printerName}\"";
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"没有可用于打印「{file.Name}」的默认程序:{ex.Message}。请安装 PDF 阅读器(如 Edge/Adobe Acrobat)并为其注册「打印」关联后重试。", ex);
        }

        if (process is null)
        {
            throw new InvalidOperationException(
                $"系统未能为「{file.Name}」启动打印程序。请确认已安装可打开该类型文件的程序。");
        }

        using (process)
        {
            if (!process.WaitForExit(ShellTimeoutMilliseconds))
            {
                TryKill(process);
                throw new TimeoutException(
                    $"调用外部程序打印「{file.Name}」超过 {ShellTimeoutMilliseconds / 1000} 秒仍未结束,已终止。请改为在打印机自带驱动中直接打印,或手动打开文件后使用 Ctrl+P。");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"外部打印程序返回错误(退出码 {process.ExitCode}),「{file.Name}」可能未发送成功。请检查默认打印程序设置后重试。");
            }
        }
    }

    /// <summary>按文件类型自动分发;不支持的类型抛出中文异常。</summary>
    public static void Print(string printerName, string filePath)
    {
        PrintingErrors.RequirePrinterName(printerName);
        var file = PrintingErrors.RequireFile(filePath);

        switch (DetectKind(file.FullName))
        {
            case FilePrintKind.Text:
                PrintText(printerName, file.FullName);
                break;
            case FilePrintKind.Image:
                PrintImage(printerName, file.FullName);
                break;
            case FilePrintKind.Xps:
                PrintXps(printerName, file.FullName);
                break;
            case FilePrintKind.Pdf:
                PrintWithShell(file.FullName, printerName);
                break;
            case FilePrintKind.Raw:
                PrintRaw(printerName, file.FullName);
                break;
            default:
                var extension = Path.GetExtension(file.FullName);
                throw new InvalidOperationException(
                    $"不支持打印「{(string.IsNullOrEmpty(extension) ? "(无扩展名)" : extension)}」类型的文件。当前支持:文本(.txt/.log/.csv/.md/.json/.xml)、图片(.png/.jpg/.bmp/.gif/.tif)、XPS(.xps/.oxps)、PDF(.pdf)与 RAW(.bin/.prn/.ps/.pcl/.zpl)。请先转换为 PDF 或图片后再试。");
        }
    }

    private static string ReadAllText(string path, Encoding? encoding)
    {
        if (encoding is not null)
        {
            return File.ReadAllText(path, encoding);
        }

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static FixedDocument BuildTextDocument(string title, string text, string? fontFamily)
    {
        var contentWidth = PageMetrics.A4Width - (TextMargin * 2);
        var typeface = new Typeface(
            new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? DefaultFont.Source : fontFamily),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        var culture = CultureInfo.CurrentUICulture;

        FormattedText Format(string value) => new(
            value.Length == 0 ? " " : value,
            culture,
            FlowDirection.LeftToRight,
            typeface,
            11,
            Brushes.Black,
            1.0)
        {
            MaxTextWidth = contentWidth,
            TextAlignment = TextAlignment.Left,
            Trimming = TextTrimming.None,
        };

        var pages = new List<List<FormattedText>>();
        var current = new List<FormattedText>();
        var used = 0.0;
        double TopOf(int pageIndex) => pageIndex == 0 ? TextMargin + 34 : TextMargin;
        var available = PageMetrics.A4Height - TopOf(0) - TextMargin;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var raw in normalized.Split('\n'))
        {
            var formatted = Format(raw.Replace("\t", "    "));
            if (current.Count > 0 && used + formatted.Height > available)
            {
                pages.Add(current);
                current = new List<FormattedText>();
                used = 0;
                available = PageMetrics.A4Height - TopOf(1) - TextMargin;
            }

            current.Add(formatted);
            used += formatted.Height;
        }

        pages.Add(current);

        var document = new FixedDocument();
        for (var i = 0; i < pages.Count; i++)
        {
            var top = TopOf(i);
            var page = new FixedPage
            {
                Width = PageMetrics.A4Width,
                Height = PageMetrics.A4Height,
                Background = Brushes.White,
            };

            var canvas = new Canvas { Width = PageMetrics.A4Width, Height = PageMetrics.A4Height };
            page.Children.Add(canvas);

            if (i == 0)
            {
                var header = new TextBlock
                {
                    Text = title,
                    FontFamily = DefaultFont,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.Black,
                    TextWrapping = TextWrapping.Wrap,
                    Width = contentWidth,
                };
                Canvas.SetLeft(header, TextMargin);
                Canvas.SetTop(header, TextMargin);
                canvas.Children.Add(header);
            }

            var visual = new TextPageVisual(pages[i])
            {
                Width = contentWidth,
                Height = PageMetrics.A4Height - top - TextMargin,
            };
            Canvas.SetLeft(visual, TextMargin);
            Canvas.SetTop(visual, top);
            canvas.Children.Add(visual);

            var footer = new TextBlock
            {
                Text = $"第 {i + 1} / {pages.Count} 页 · PrinterMate 文本打印",
                FontFamily = DefaultFont,
                FontSize = 8,
                Foreground = Brushes.Gray,
            };
            Canvas.SetLeft(footer, TextMargin);
            Canvas.SetTop(footer, PageMetrics.A4Height - TextMargin + 20);
            canvas.Children.Add(footer);

            var content = new PageContent();
            ((IAddChild)content).AddChild(page);
            document.Pages.Add(content);
        }

        return document;
    }

    private static FixedDocument BuildImageDocument(BitmapImage bitmap)
    {
        var contentWidth = PageMetrics.A4Width - (TextMargin * 2);
        var contentHeight = PageMetrics.A4Height - (TextMargin * 2);

        var page = new FixedPage
        {
            Width = PageMetrics.A4Width,
            Height = PageMetrics.A4Height,
            Background = Brushes.White,
        };

        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            Width = contentWidth,
            Height = contentHeight,
            StretchDirection = StretchDirection.DownOnly,
        };
        FixedPage.SetLeft(image, TextMargin);
        FixedPage.SetTop(image, TextMargin);
        page.Children.Add(image);

        var document = new FixedDocument();
        var content = new PageContent();
        ((IAddChild)content).AddChild(page);
        document.Pages.Add(content);
        return document;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 终止失败时不再覆盖原始错误。
        }
    }

    /// <summary>按行绘制排版结果,滚动位置由外部按 FormattedText.Height 预先算好。</summary>
    private sealed class TextPageVisual : FrameworkElement
    {
        private readonly IReadOnlyList<FormattedText> _lines;

        public TextPageVisual(IReadOnlyList<FormattedText> lines) => _lines = lines;

        protected override void OnRender(DrawingContext drawingContext)
        {
            var y = 0.0;
            foreach (var line in _lines)
            {
                drawingContext.DrawText(line, new Point(0, y));
                y += line.Height;
            }
        }
    }
}

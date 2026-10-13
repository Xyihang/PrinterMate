using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PrinterMate.Printing;

/// <summary>自绘测试页的数据模型。</summary>
public sealed record TestPageModel(
    string PrinterName, string IpAddress, string DriverName, string PortName, string Pdl,
    string ColorMode, string PageSize, DateTimeOffset Timestamp,
    IReadOnlyList<KeyValuePair<string, string>> ExtraLines);

/// <summary>用 WPF 矢量绘图生成测试页,不依赖任何打印机驱动。</summary>
public static class TestPageRenderer
{
    private const double Margin = 36;
    private const double ContentWidth = PageMetrics.A4Width - (Margin * 2);

    private static readonly FontFamily DefaultFont =
        new("Microsoft YaHei UI, Microsoft YaHei, Segoe UI, Arial");

    private static readonly Brush TitleBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x37));
    private static readonly Brush BodyBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x0B, 0x63, 0xC4));
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xDE));
    private static readonly Brush CrossBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly Brush BorderBrush = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));

    /// <summary>用真实设备无关色值定义的色块:名称、色号、sRGB 颜色。</summary>
    private sealed record Swatch(string Name, string Code, Color Color);

    private static readonly Swatch[] RgbSwatches =
    {
        new("红 Red", "#FF0000 / 255,0,0", Color.FromRgb(0xFF, 0x00, 0x00)),
        new("绿 Green", "#00FF00 / 0,255,0", Color.FromRgb(0x00, 0xFF, 0x00)),
        new("蓝 Blue", "#0000FF / 0,0,255", Color.FromRgb(0x00, 0x00, 0xFF)),
        new("青 Cyan", "#00FFFF / 0,255,255", Color.FromRgb(0x00, 0xFF, 0xFF)),
        new("品红 Magenta", "#FF00FF / 255,0,255", Color.FromRgb(0xFF, 0x00, 0xFF)),
        new("黄 Yellow", "#FFFF00 / 255,255,0", Color.FromRgb(0xFF, 0xFF, 0x00)),
        new("黑 Black", "#000000 / 0,0,0", Color.FromRgb(0x00, 0x00, 0x00)),
        new("白 White", "#FFFFFF / 255,255,255", Color.FromRgb(0xFF, 0xFF, 0xFF)),
    };

    private static readonly Swatch[] CmykSwatches =
    {
        new("青 C 100%", "#00AEEF", Color.FromRgb(0x00, 0xAE, 0xEF)),
        new("品红 M 100%", "#EC008C", Color.FromRgb(0xEC, 0x00, 0x8C)),
        new("黄 Y 100%", "#FFF200", Color.FromRgb(0xFF, 0xF2, 0x00)),
        new("黑 K 100%", "#231F20", Color.FromRgb(0x23, 0x1F, 0x20)),
        new("青+品红 C+M", "#0033A0", Color.FromRgb(0x00, 0x33, 0xA0)),
        new("青+黄 C+Y", "#00A651", Color.FromRgb(0x00, 0xA6, 0x51)),
        new("品红+黄 M+Y", "#ED1C24", Color.FromRgb(0xED, 0x1C, 0x24)),
        new("青+品红+黄 C+M+Y", "#1A1A1A", Color.FromRgb(0x1A, 0x1A, 0x1A)),
    };

    private static readonly Swatch[] GraySwatches = BuildGraySwatches();

    /// <summary>A4、96 DPI 的 FixedDocument,内容含:标题、设备信息表、RGB/CMYK 色条、灰阶条、对齐网格与十字线、页面设置项、时间戳。</summary>
    public static FixedDocument BuildDocument(TestPageModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var page = new FixedPage
        {
            Width = PageMetrics.A4Width,
            Height = PageMetrics.A4Height,
            Background = Brushes.White,
        };

        var canvas = new Canvas { Width = PageMetrics.A4Width, Height = PageMetrics.A4Height };
        page.Children.Add(canvas);

        var y = 30.0;
        y = DrawHeader(canvas, model, y);
        y = DrawDeviceInfo(canvas, model, y);
        y = DrawColorBars(canvas, y);
        y = DrawAlignmentGrid(canvas, y);
        y = DrawPageSettings(canvas, model, y);
        DrawFooter(canvas, model);

        var document = new FixedDocument();
        var content = new PageContent();
        ((IAddChild)content).AddChild(page);
        document.Pages.Add(content);
        return document;
    }

    /// <summary>通过 XpsDocumentWriter 投递到指定打印队列。</summary>
    public static void Print(string printerName, TestPageModel model)
    {
        PrintingErrors.RequirePrinterName(printerName);
        ArgumentNullException.ThrowIfNull(model);

        PrintCore.Write(printerName, BuildDocument(model));
    }

    private static double DrawHeader(Canvas canvas, TestPageModel model, double y)
    {
        AddText(canvas, "PrinterMate 打印测试页", Margin, y, 24, TitleBrush, bold: true);
        y += 34;
        AddText(canvas, "自绘矢量测试页 · 用于验证驱动解析、色彩还原、机械对齐与纸张设置", Margin, y, 11, MutedBrush);
        y += 20;
        AddText(canvas, $"目标打印机:{model.PrinterName}", Margin, y, 12, AccentBrush, bold: true);
        y += 20;
        AddLine(canvas, Margin, y, Margin + ContentWidth, y, BorderBrush, 1);
        return y + 12;
    }

    private static double DrawDeviceInfo(Canvas canvas, TestPageModel model, double y)
    {
        y = DrawSectionTitle(canvas, "一、设备与连接信息", y);

        var rows = new List<KeyValuePair<string, string>>
        {
            new("打印机名称", model.PrinterName),
            new("IP 地址", model.IpAddress),
            new("驱动程序", model.DriverName),
            new("端口", model.PortName),
            new("页面描述语言", model.Pdl),
            new("色彩模式", model.ColorMode),
            new("纸张规格", model.PageSize),
            new("生成时间", model.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")),
        };

        if (model.ExtraLines is { Count: > 0 })
        {
            foreach (var line in model.ExtraLines)
            {
                rows.Add(line);
            }
        }

        foreach (var row in rows)
        {
            AddText(canvas, string.IsNullOrEmpty(row.Key) ? "-" : row.Key, Margin, y, 10.5, MutedBrush, bold: true);
            AddText(canvas, string.IsNullOrEmpty(row.Value) ? "(未提供)" : row.Value, Margin + 120, y, 10.5, BodyBrush, maxWidth: ContentWidth - 120);
            y += 22;
        }

        return y + 6;
    }

    private static double DrawColorBars(Canvas canvas, double y)
    {
        y = DrawSectionTitle(canvas, "二、色彩测试", y);

        AddText(canvas, "RGB 三原色与二次色(设备无关 sRGB,标注 0-255 与十六进制色号)", Margin, y, 10, BodyBrush);
        y += 18;
        y = DrawSwatchRow(canvas, RgbSwatches, y, 52);
        y += 10;

        AddText(canvas, "CMYK 印刷四色(标注 C/M/Y/K 百分比与近似 sRGB 色号)", Margin, y, 10, BodyBrush);
        y += 18;
        y = DrawSwatchRow(canvas, CmykSwatches, y, 52);
        y += 10;

        AddText(canvas, "灰阶(0% → 100%,标注百分比与 0-255 灰值)", Margin, y, 10, BodyBrush);
        y += 18;
        y = DrawSwatchRow(canvas, GraySwatches, y, 44);
        return y + 8;
    }

    private static double DrawSwatchRow(Canvas canvas, Swatch[] swatches, double y, double height)
    {
        var gap = 6.0;
        var width = (ContentWidth - (gap * (swatches.Length - 1))) / swatches.Length;
        var x = Margin;

        foreach (var swatch in swatches)
        {
            var rect = new Rectangle
            {
                Width = width,
                Height = height,
                Fill = new SolidColorBrush(swatch.Color),
                Stroke = BorderBrush,
                StrokeThickness = 0.8,
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            canvas.Children.Add(rect);

            AddText(canvas, swatch.Name, x, y + height + 3, 8.5, BodyBrush, bold: true, maxWidth: width, align: TextAlignment.Center);
            AddText(canvas, swatch.Code, x, y + height + 15, 7.5, MutedBrush, maxWidth: width, align: TextAlignment.Center);

            x += width + gap;
        }

        return y + height + 30;
    }

    private static double DrawAlignmentGrid(Canvas canvas, double y)
    {
        y = DrawSectionTitle(canvas, "三、对齐与套准", y);

        const double gridHeight = 200;
        const double spacing = 37.8; // 10mm @ 96 DPI

        var left = Margin;
        var right = Margin + ContentWidth;
        var top = y;
        var bottom = y + gridHeight;

        var grid = new Rectangle
        {
            Width = ContentWidth,
            Height = gridHeight,
            Stroke = BorderBrush,
            StrokeThickness = 1,
        };
        Canvas.SetLeft(grid, left);
        Canvas.SetTop(grid, top);
        canvas.Children.Add(grid);

        for (var x = left + spacing; x < right; x += spacing)
        {
            AddLine(canvas, x, top, x, bottom, GridBrush, 0.6);
        }

        for (var gy = top + spacing; gy < bottom; gy += spacing)
        {
            AddLine(canvas, left, gy, right, gy, GridBrush, 0.6);
        }

        var centerX = left + (ContentWidth / 2);
        var centerY = top + (gridHeight / 2);

        AddLine(canvas, centerX, top, centerX, bottom, CrossBrush, 0.8);
        AddLine(canvas, left, centerY, right, centerY, CrossBrush, 0.8);

        var circle = new Ellipse
        {
            Width = 50,
            Height = 50,
            Stroke = CrossBrush,
            StrokeThickness = 1,
        };
        Canvas.SetLeft(circle, centerX - 25);
        Canvas.SetTop(circle, centerY - 25);
        canvas.Children.Add(circle);

        AddRegistrationCross(canvas, left + 14, top + 14);
        AddRegistrationCross(canvas, right - 14, top + 14);
        AddRegistrationCross(canvas, left + 14, bottom - 14);
        AddRegistrationCross(canvas, right - 14, bottom - 14);

        AddText(canvas, $"网格间距 10mm(96 DPI ≈ {spacing:0.0} 单位),中心十字为页面几何中心", Margin, bottom + 4, 9, MutedBrush);
        return bottom + 24;
    }

    private static double DrawPageSettings(Canvas canvas, TestPageModel model, double y)
    {
        y = DrawSectionTitle(canvas, "四、页面设置项", y);

        var rows = new List<KeyValuePair<string, string>>
        {
            new("纸张 / 方向", $"{model.PageSize} / 纵向(Portrait)"),
            new("色彩模式", model.ColorMode),
            new("页面描述语言", model.Pdl),
            new("渲染分辨率", "96 DPI(WPF 设备无关单位,由驱动栅格化为设备分辨率)"),
            new("边距", $"左右 {Margin:0} / 上下 {Margin:0}(96 DPI 单位)"),
        };

        foreach (var row in rows)
        {
            AddText(canvas, row.Key, Margin, y, 10.5, MutedBrush, bold: true);
            AddText(canvas, row.Value, Margin + 120, y, 10.5, BodyBrush, maxWidth: ContentWidth - 120);
            y += 22;
        }

        return y + 6;
    }

    private static void DrawFooter(Canvas canvas, TestPageModel model)
    {
        var y = PageMetrics.A4Height - 34;
        AddLine(canvas, Margin, y - 8, Margin + ContentWidth, y - 8, BorderBrush, 1);
        AddText(
            canvas,
            $"生成时间(本地):{model.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  PrinterMate 打印测试页 v1",
            Margin,
            y,
            9,
            MutedBrush);
    }

    private static double DrawSectionTitle(Canvas canvas, string title, double y)
    {
        AddText(canvas, title, Margin, y, 13, TitleBrush, bold: true);
        return y + 26;
    }

    private static void AddRegistrationCross(Canvas canvas, double x, double y)
    {
        const double arm = 9;
        AddLine(canvas, x - arm, y, x + arm, y, CrossBrush, 1);
        AddLine(canvas, x, y - arm, x, y + arm, CrossBrush, 1);
    }

    private static TextBlock AddText(
        Canvas canvas,
        string text,
        double x,
        double y,
        double size,
        Brush brush,
        bool bold = false,
        double maxWidth = double.PositiveInfinity,
        TextAlignment align = TextAlignment.Left)
    {
        var block = new TextBlock
        {
            Text = text ?? string.Empty,
            FontFamily = DefaultFont,
            FontSize = size,
            Foreground = brush,
            TextAlignment = align,
        };

        if (bold)
        {
            block.FontWeight = FontWeights.Bold;
        }

        if (!double.IsPositiveInfinity(maxWidth))
        {
            block.Width = maxWidth;
            block.TextWrapping = TextWrapping.Wrap;
        }

        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        canvas.Children.Add(block);
        return block;
    }

    private static void AddLine(Canvas canvas, double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        var line = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = thickness,
        };
        canvas.Children.Add(line);
    }

    private static Swatch[] BuildGraySwatches()
    {
        var result = new Swatch[11];
        for (var i = 0; i <= 10; i++)
        {
            var percent = i * 10;
            var value = (byte)Math.Round(255.0 * (100 - percent) / 100.0);
            result[i] = new Swatch(
                $"{percent}%",
                $"#{value:X2}{value:X2}{value:X2} / {value}",
                Color.FromRgb(value, value, value));
        }

        return result;
    }
}

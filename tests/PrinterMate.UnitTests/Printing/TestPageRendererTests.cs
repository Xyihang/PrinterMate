using System.Runtime.ExceptionServices;
using System.Windows.Documents;
using PrinterMate.Printing;
using Xunit;

namespace PrinterMate.UnitTests.Printing;

public class TestPageRendererTests
{
    private static TestPageModel CreateModel() => new(
        "PrinterMate-Test",
        "192.168.1.50",
        "Generic / Text Only",
        "IP_192.168.1.50",
        "PCL6",
        "彩色",
        "A4",
        DateTimeOffset.Parse("2024-01-02T03:04:05+08:00"),
        new List<KeyValuePair<string, string>>
        {
            new("固件版本", "1.2.3"),
            new("序列号", "SN-TEST-0001"),
        });

    [Fact]
    public void BuildDocument_returns_single_page_a4_document() => RunOnSta(() =>
    {
        var document = TestPageRenderer.BuildDocument(CreateModel());

        Assert.NotNull(document);
        Assert.Equal(1, document.Pages.Count);

        var content = document.Pages[0];
        Assert.NotNull(content);
        Assert.NotNull(content.Child);

        var page = content.Child;
        Assert.Equal(793.7, page.Width, 1);
        Assert.Equal(1122.5, page.Height, 1);
        Assert.NotEmpty(page.Children);
    });

    [Fact]
    public void BuildDocument_draws_color_swatches_and_grid() => RunOnSta(() =>
    {
        var document = TestPageRenderer.BuildDocument(CreateModel());
        var elements = Flatten(document.Pages[0].Child).ToList();

        var rectangles = elements.OfType<System.Windows.Shapes.Rectangle>().Count();
        var ellipses = elements.OfType<System.Windows.Shapes.Ellipse>().Count();
        var lines = elements.OfType<System.Windows.Shapes.Line>().Count();
        var texts = elements.OfType<System.Windows.Controls.TextBlock>().Count();

        // RGB 8 + CMYK 8 + 灰阶 11 个色块,外加对齐网格外框。
        Assert.True(rectangles >= 27, $"色块/网格矩形数量不足:{rectangles}");
        Assert.True(lines > 20, $"网格线数量不足:{lines}");
        Assert.Equal(1, ellipses);
        Assert.True(texts > 20, $"文字标注数量不足:{texts}");
    });

    [Fact]
    public void BuildDocument_rejects_null_model() => RunOnSta(() =>
        Assert.Throws<ArgumentNullException>(() => TestPageRenderer.BuildDocument(null!)));

    [Fact]
    public void Print_rejects_empty_printer_name() => RunOnSta(() =>
    {
        var ex = Assert.Throws<ArgumentException>(() => TestPageRenderer.Print("  ", CreateModel()));
        Assert.Contains("打印机名称为空", ex.Message);
    });

    private static IEnumerable<System.Windows.DependencyObject> Flatten(System.Windows.DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is System.Windows.DependencyObject element)
            {
                yield return element;
                foreach (var nested in Flatten(element))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>WPF 对象有线程亲和性,xUnit 默认线程为 MTA,因此构建与断言都在同一条 STA 线程上完成。</summary>
    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
    }
}

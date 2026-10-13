using PrinterMate.Printing;
using Xunit;

namespace PrinterMate.UnitTests.Printing;

public class FilePrintServiceTests
{
    [Theory]
    [InlineData(".txt", FilePrintKind.Text)]
    [InlineData(".TXT", FilePrintKind.Text)]
    [InlineData(".log", FilePrintKind.Text)]
    [InlineData(".png", FilePrintKind.Image)]
    [InlineData(".jpg", FilePrintKind.Image)]
    [InlineData(".jpeg", FilePrintKind.Image)]
    [InlineData(".xps", FilePrintKind.Xps)]
    [InlineData(".oxps", FilePrintKind.Xps)]
    [InlineData(".pdf", FilePrintKind.Pdf)]
    [InlineData(".bin", FilePrintKind.Raw)]
    [InlineData(".prn", FilePrintKind.Raw)]
    [InlineData(".zpl", FilePrintKind.Raw)]
    [InlineData(".docx", FilePrintKind.Unsupported)]
    [InlineData(".exe", FilePrintKind.Unsupported)]
    public void DetectKind_uses_the_file_extension(string extension, FilePrintKind expected)
    {
        Assert.Equal(expected, FilePrintService.DetectKind("C:\\temp\\sample" + extension));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\temp\\no-extension")]
    public void DetectKind_returns_unsupported_for_useless_paths(string path)
    {
        Assert.Equal(FilePrintKind.Unsupported, FilePrintService.DetectKind(path));
    }

    [Fact]
    public void DetectKind_does_not_require_the_file_to_exist()
    {
        Assert.Equal(FilePrintKind.Text, FilePrintService.DetectKind("C:\\totally\\missing\\file.txt"));
    }

    [Fact]
    public void IsSupported_reflects_DetectKind()
    {
        Assert.True(FilePrintService.IsSupported("a.pdf"));
        Assert.True(FilePrintService.IsSupported("a.png"));
        Assert.False(FilePrintService.IsSupported("a.docx"));
        Assert.False(FilePrintService.IsSupported(""));
        Assert.False(FilePrintService.IsSupported(null!));
    }

    [Fact]
    public void Print_rejects_empty_printer_name()
    {
        var ex = Assert.Throws<ArgumentException>(() => FilePrintService.Print("  ", "a.txt"));
        Assert.Contains("打印机名称为空", ex.Message);
    }

    [Fact]
    public void Print_rejects_missing_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        var ex = Assert.Throws<FileNotFoundException>(() => FilePrintService.Print("PrinterMate-Test", missing));
        Assert.Contains("找不到文件", ex.Message);
    }

    [Fact]
    public void Print_rejects_unsupported_extension_with_chinese_message()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".docx");
        File.WriteAllText(path, "test");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => FilePrintService.Print("PrinterMate-Test", path));
            Assert.Contains("不支持打印", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PrintText_rejects_missing_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        Assert.Throws<FileNotFoundException>(() => FilePrintService.PrintText("PrinterMate-Test", missing));
    }

    [Fact]
    public void PrintWithShell_rejects_missing_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        Assert.Throws<FileNotFoundException>(() => FilePrintService.PrintWithShell(missing));
    }

    [Fact]
    public void PrintRaw_rejects_empty_file()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, Array.Empty<byte>());
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => FilePrintService.PrintRaw("PrinterMate-Test", path));
            Assert.Contains("内容为空", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

using System.Text.Json;
using PrinterMate.Security;
using Xunit;

namespace PrinterMate.UnitTests.Security;

public class AuditLogTests : IDisposable
{
    private readonly string _directory;

    public AuditLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pm-sec-audit", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void Write_ThenRead_RoundTripsEntry()
    {
        var log = new AuditLog(_directory);
        log.Write(new AuditEntry(
            DateTimeOffset.Now,
            "download.done",
            "https://ftp.hp.com/a.exe",
            "成功",
            "https://ftp.hp.com/a.exe",
            new string('a', 64),
            "CN=HP Inc.",
            "已保存",
            "tester"));

        var entries = log.Read(DateOnly.FromDateTime(DateTime.Now));

        var entry = Assert.Single(entries);
        Assert.Equal("download.done", entry.Operation);
        Assert.Equal("https://ftp.hp.com/a.exe", entry.Target);
        Assert.Equal("成功", entry.Outcome);
        Assert.Equal(new string('a', 64), entry.Sha256);
        Assert.Equal("CN=HP Inc.", entry.SignerSubject);
        Assert.Equal("tester", entry.User);
    }

    [Fact]
    public void Write_OverloadBuildsEntryAndWritesJsonl()
    {
        var log = new AuditLog(_directory);
        log.Write("download.source", "https://ftp.hp.com/a.exe", "通过", "来源校验通过", "https://ftp.hp.com/a.exe");

        var entries = log.Read(DateOnly.FromDateTime(DateTime.Now));

        var entry = Assert.Single(entries);
        Assert.Equal("download.source", entry.Operation);
        Assert.NotEmpty(entry.User);
        Assert.True(File.Exists(log.CurrentFile));

        foreach (var line in File.ReadAllLines(log.CurrentFile))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            Assert.Equal("download.source", doc.RootElement.GetProperty("Operation").GetString());
        }
    }

    [Fact]
    public void Read_ReturnsEmptyForDayWithoutFile()
    {
        var log = new AuditLog(_directory);

        Assert.Empty(log.Read(DateOnly.FromDateTime(DateTime.Now.AddDays(-5))));
    }

    [Fact]
    public void Export_WritesExistingFile()
    {
        var log = new AuditLog(_directory);
        log.Write("download.done", "https://ftp.hp.com/a.exe", "成功", "完成");

        var destination = Path.Combine(_directory, "export", "audit-export.jsonl");
        var exported = log.Export(destination);

        Assert.True(File.Exists(exported));
        Assert.True(new FileInfo(exported).Length > 0);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

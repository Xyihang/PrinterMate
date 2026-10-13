using PrinterMate.Printing;
using Xunit;

namespace PrinterMate.UnitTests.Printing;

public class PrinterStatusMapperTests
{
    [Theory]
    [InlineData(0x00000000, 0, PrinterHealth.Online)]
    [InlineData(0x00000000, 3, PrinterHealth.Printing)]
    [InlineData(0x00000001, 0, PrinterHealth.Paused)]
    [InlineData(0x00000002, 0, PrinterHealth.Error)]
    [InlineData(0x00000008, 0, PrinterHealth.PaperJam)]
    [InlineData(0x00000010, 0, PrinterHealth.PaperOut)]
    [InlineData(0x00000080, 0, PrinterHealth.Offline)]
    [InlineData(0x02000000, 0, PrinterHealth.Offline)]
    [InlineData(0x00001000, 0, PrinterHealth.NotAvailable)]
    [InlineData(0x00000400, 0, PrinterHealth.Printing)]
    [InlineData(0x00000200, 0, PrinterHealth.Busy)]
    [InlineData(0x00020000, 0, PrinterHealth.TonerLow)]
    [InlineData(0x00040000, 0, PrinterHealth.NoToner)]
    [InlineData(0x00400000, 0, PrinterHealth.DoorOpen)]
    [InlineData(0x00200000, 0, PrinterHealth.Error)]
    [InlineData(0x01000000, 0, PrinterHealth.Online)]
    public void MapPrinterHealth_maps_known_status_bits(int status, int jobCount, PrinterHealth expected)
    {
        Assert.Equal(expected, PrinterStatusMapper.MapPrinterHealth(status, jobCount));
    }

    [Theory]
    [InlineData(0x00000008 | 0x00000010, PrinterHealth.PaperJam)]
    [InlineData(0x00000001 | 0x00000010, PrinterHealth.PaperOut)]
    [InlineData(0x00040000 | 0x00020000, PrinterHealth.NoToner)]
    [InlineData(0x00000004 | 0x00000200, PrinterHealth.Busy)]
    public void MapPrinterHealth_prefers_the_more_actionable_state(int status, PrinterHealth expected)
    {
        Assert.Equal(expected, PrinterStatusMapper.MapPrinterHealth(status, 0));
    }

    [Fact]
    public void MapPrinterHealth_returns_unknown_for_unknown_bits()
    {
        Assert.Equal(PrinterHealth.Unknown, PrinterStatusMapper.MapPrinterHealth(0x40000000, 0));
    }

    [Theory]
    [InlineData(0x00000001, PrinterHealth.Paused)]
    [InlineData(0x00000002, PrinterHealth.Error)]
    [InlineData(0x00000200, PrinterHealth.Error)]
    [InlineData(0x00000400, PrinterHealth.Error)]
    [InlineData(0x00000040, PrinterHealth.PaperOut)]
    [InlineData(0x00000020, PrinterHealth.Offline)]
    [InlineData(0x00000010, PrinterHealth.Printing)]
    [InlineData(0x00000008, PrinterHealth.Busy)]
    [InlineData(0x00000080, PrinterHealth.Online)]
    [InlineData(0x00001000, PrinterHealth.Online)]
    [InlineData(0x00000000, PrinterHealth.Unknown)]
    public void MapJobHealth_maps_known_status_bits(int status, PrinterHealth expected)
    {
        Assert.Equal(expected, PrinterStatusMapper.MapJobHealth(status));
    }

    [Fact]
    public void Describe_and_Suggest_are_non_empty_for_every_health()
    {
        foreach (var health in Enum.GetValues<PrinterHealth>())
        {
            Assert.False(string.IsNullOrWhiteSpace(PrinterStatusMapper.Describe(health)), $"Describe({health}) 为空");
            Assert.False(string.IsNullOrWhiteSpace(PrinterStatusMapper.Suggest(health)), $"Suggest({health}) 为空");
        }
    }

    [Theory]
    [InlineData(PrinterHealth.PaperOut, "缺纸")]
    [InlineData(PrinterHealth.PaperJam, "卡纸")]
    [InlineData(PrinterHealth.Paused, "已暂停")]
    [InlineData(PrinterHealth.Offline, "脱机")]
    [InlineData(PrinterHealth.Online, "就绪")]
    public void Describe_returns_expected_chinese_phrase(PrinterHealth health, string expected)
    {
        Assert.Equal(expected, PrinterStatusMapper.Describe(health));
    }
}

using PrinterMate.Catalog;
using PrinterMate.Core.Drivers;
using PrinterMate.Security;
using Xunit;
using Xunit.Abstractions;

namespace PrinterMate.IntegrationTests;

/// <summary>
/// 批量下载运行器(用 dotnet test 驱动的一次性批处理入口)。
///
/// 行为边界:
///   · **只下载**到 %LOCALAPPDATA%\PrinterMate\Downloads,**绝不自动导入**驱动库;
///   · 全程只走白名单官方源(Microsoft Update Catalog),每个包都强制 HTTPS + 官方 SHA256 + 数字签名;
///   · 不打包、不修改厂商文件,拿到的就是厂商原始包。
///
/// 许可与合规:本运行器代表**本机使用者本人**的显式授权 ——
/// 使用者在会话中明确表示「仅自己使用、不对外分发」,授权记录会写入审计日志。
/// 若将来要把程序分发给他人,不得连带分发 %LOCALAPPDATA%\PrinterMate 下的任何驱动文件。
///
/// 用环境变量 PM_FILL_BRANDS 指定品牌(逗号分隔);不设则处理所有"没有可用条目"的品牌。
/// 用 PM_FILL_PER_BRAND 控制每品牌最多尝试几个候选(默认 3)。
/// </summary>
public class DriverDownloadRunner
{
    private const string UserConsentNote =
        "本机使用者本人明确授权:仅自己使用、不对外分发。";

    private readonly ITestOutputHelper _output;

    public DriverDownloadRunner(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task 批量下载驱动到下载目录_不导入()
    {
        var library = DriverLibrary.Load();
        var status = library.GetStatus(DriverMatchEngine.CurrentArchitecture);

        _output.WriteLine($"驱动库根目录 : {library.Root}");
        _output.WriteLine($"现有条目     : {library.Entries.Count}(可直接使用 {status.ReadyEntries} 条)");
        _output.WriteLine($"下载目录     : {OnlineDriverInstaller.DownloadDirectory}");
        _output.WriteLine(new string('=', 90));

        var requested = Environment.GetEnvironmentVariable("PM_FILL_BRANDS");
        var perBrand = int.TryParse(Environment.GetEnvironmentVariable("PM_FILL_PER_BRAND"), out var n)
            ? Math.Clamp(n, 1, 6)
            : 3;

        var brands = string.IsNullOrWhiteSpace(requested)
            ? BrandsWithoutAvailableDriver(library)
            : requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        _output.WriteLine($"本次品牌({brands.Count}):{string.Join(", ", brands)}");
        _output.WriteLine(new string('=', 90));

        var installer = new OnlineDriverInstaller(library: library);

        // 代表使用者本人记录这次授权(合规凭据)
        installer.RecordLicenseAcceptance(
            string.Join(", ", brands),
            "https://www.catalog.update.microsoft.com/",
            "各品牌厂商官方许可页(见「在线补齐」区的「查看厂商官方页」按钮)");

        var progress = new Progress<string>(text => _output.WriteLine(text));
        var results = await installer.DownloadAllAsync(brands, perBrand, progress);

        _output.WriteLine(new string('=', 90));

        foreach (var group in results.GroupBy(r => r.Success))
        {
            _output.WriteLine(group.Key ? "下载成功:" : "未下载:");
            foreach (var item in group)
            {
                _output.WriteLine($"  [{(item.Success ? "✅" : "⚠")}] {item.Name}:{item.Message}");
            }
        }

        _output.WriteLine(new string('=', 90));
        _output.WriteLine($"成功 {results.Count(r => r.Success)} 个 / 共 {results.Count} 个品牌");
        _output.WriteLine("注意:文件只落在下载目录,**没有**导入驱动库;");
        _output.WriteLine("要入库请在程序里点「批量导入一个文件夹…」并选择下载目录。");
        _output.WriteLine($"授权记录:{UserConsentNote}");

        Assert.NotNull(library.Root);
    }

    /// <summary>找出"目前没有任何可用驱动"的品牌(排除微软免驱与已入库的品牌)。</summary>
    private static List<string> BrandsWithoutAvailableDriver(DriverLibrary library)
        => library.Brands
            .Where(brand => !library.ForBrand(brand).Any(e =>
                e.IsInboxDriver
                || library.CheckAvailability(e, DriverMatchEngine.CurrentArchitecture, DriverMatchEngine.CurrentOsTag).Available))
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

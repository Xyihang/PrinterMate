using PrinterMate.Catalog;
using Xunit;

namespace PrinterMate.UnitTests.Core;

/// <summary>
/// 测试用的隔离策略工厂:每个用例拿到一个不存在的策略文件路径,
/// 既不会读到开发机上真实的 %LOCALAPPDATA% 策略,也不会互相污染。
/// </summary>
internal static class TestPolicies
{
    public static DistributionPolicy Isolated()
        => new(Path.Combine(Path.GetTempPath(), "pm-policy-" + Guid.NewGuid().ToString("N") + ".json"));
}

public class DistributionPolicyTests
{
    [Fact]
    public void 默认策略必须全部关闭()
    {
        var policy = TestPolicies.Isolated();

        Assert.False(policy.AllowsAll);
        Assert.False(policy.IsEnabled("HP"));
        Assert.False(policy.IsEnabled("Canon"));
    }

    [Fact]
    public void 开启全部品牌后所有品牌都可用()
    {
        var policy = TestPolicies.Isolated();
        policy.SetAll(true, "个人自用,不分发");

        Assert.True(policy.IsEnabled("HP"));
        Assert.True(policy.IsEnabled("Canon"));
        Assert.Contains("个人自用", policy.Describe());
    }

    [Fact]
    public void 单独开启某品牌不影响其它品牌()
    {
        var policy = TestPolicies.Isolated();
        policy.SetBrand("HP", true, "个人自用");

        Assert.True(policy.IsEnabled("HP"));
        Assert.False(policy.IsEnabled("Canon"));
    }

    [Fact]
    public void 品牌设置应覆盖全局设置且可回退()
    {
        var policy = TestPolicies.Isolated();
        policy.SetAll(true, "全部开启");
        policy.SetBrand("Epson", false, "全部开启");

        Assert.True(policy.IsEnabled("HP"));
        Assert.False(policy.IsEnabled("Epson"));

        policy.ResetBrand("Epson");
        Assert.True(policy.IsEnabled("Epson"));
    }

    [Fact]
    public void 策略必须落盘并且可被新的实例读回()
    {
        var path = Path.Combine(Path.GetTempPath(), "pm-policy-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var first = new DistributionPolicy(path);
            first.SetBrand("Brother", true, "个人自用");

            var second = new DistributionPolicy(path);
            Assert.True(second.IsEnabled("Brother"));
            Assert.False(second.IsEnabled("HP"));
            Assert.Contains("Brother", second.Describe());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void 清单红线未确认时本机可显式开启_并给出提示()
    {
        using var sandbox = SandboxLibrary.Create(sha256X64: new string('A', 64), redistributable: false);

        // 未开启:拒绝,且原因里要告诉用户在哪里开启
        var blocked = sandbox.Library.CheckAvailability(sandbox.Entry, "x64");
        Assert.False(blocked.Available);
        Assert.Contains("授权", blocked.Reason);
        Assert.Contains("驱动管理", blocked.Reason);

        // 开启后:允许本地安装
        sandbox.Library.SetBrandDistribution("Test", true, "仅本机个人使用");
        var allowed = sandbox.Library.CheckAvailability(sandbox.Entry, "x64");
        Assert.True(allowed.Available);
    }

    [Fact]
    public void 本机开启开关不会放宽SHA256与签名要求()
    {
        using var sandbox = SandboxLibrary.Create(sha256X64: string.Empty, redistributable: false);

        sandbox.Library.SetBrandDistribution("Test", true, "仅本机个人使用");
        var availability = sandbox.Library.CheckAvailability(sandbox.Entry, "x64");

        Assert.False(availability.Available);
        Assert.Contains("SHA256", availability.Reason);
    }

    [Fact]
    public void 开启内置驱动后_拒绝原因应从授权变为缺少驱动包()
    {
        var library = DriverLibrary.Load(policy: TestPolicies.Isolated());
        var hp = library.Entries.First(e => e.Brand == "HP" && e.Pdl == PrintDescriptionLanguage.PCL6);

        // 第一道闸门:清单红线未确认
        var before = library.CheckAvailability(hp, "x64");
        Assert.False(before.Available);
        Assert.Contains("授权", before.Reason);

        // 本机开启后,闸门移到"驱动包是否入库"
        library.SetBrandDistribution("HP", true, "仅本机个人使用");
        var after = library.CheckAvailability(hp, "x64");
        Assert.False(after.Available);
        Assert.Contains("驱动文件", after.Reason);
        Assert.DoesNotContain("授权尚未确认", after.Reason);
    }

    [Fact]
    public void 驱动库状态应包含分发策略摘要()
    {
        var status = DriverLibrary.Load(policy: TestPolicies.Isolated()).GetStatus("x64");

        Assert.False(string.IsNullOrWhiteSpace(status.DistributionSummary));
        Assert.Contains("默认关闭", status.DistributionSummary);
    }
}

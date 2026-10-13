using Microsoft.Win32;

namespace PrinterMate.Core.Infrastructure;

/// <summary>
/// Windows Protected Print Mode(WPP)检测。
/// WPP 开启后系统只允许内置类驱动,第三方 v3/v4 打印驱动安装会被拒绝,
/// 程序必须提前降级到 L0 免驱通道,而不是等到安装失败。
/// </summary>
public class WppModeDetector
{
    private const string PolicyKeyPath = @"SOFTWARE\Policies\Microsoft\Windows NT\Printers\WPP";
    private const string PolicyValueName = "WindowsProtectedPrintMode";

    /// <summary>Windows 11 24H2(26100)起,新装系统默认启用 WPP。</summary>
    private const int WppDefaultBuild = 26100;

    public virtual WppModeStatus Detect()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKeyPath, writable: false);
            if (key?.GetValue(PolicyValueName) is { } raw)
            {
                var enabled = raw switch
                {
                    int i => i != 0,
                    long l => l != 0,
                    string s => s is "1" or "true" or "True",
                    _ => false,
                };

                return enabled
                    ? new WppModeStatus(true, "组策略", $"已通过策略 {PolicyKeyPath}\\{PolicyValueName} 启用 Windows Protected Print Mode,第三方打印驱动安装将被系统拒绝。")
                    : WppModeStatus.Disabled($"策略 {PolicyKeyPath}\\{PolicyValueName} 明确设置为关闭。");
            }
        }
        catch (Exception ex)
        {
            return new WppModeStatus(false, "检测异常", $"读取 WPP 策略失败:{ex.Message}");
        }

        var build = Environment.OSVersion.Version.Build;
        if (build >= WppDefaultBuild)
        {
            return new WppModeStatus(
                false,
                "系统版本推断",
                $"当前系统内部版本 {build} 属于 Windows 11 24H2 及以后版本,新安装的系统可能默认启用 Windows Protected Print Mode。未检测到显式策略,程序将优先使用 L0 免驱通道,并在第三方驱动安装失败时自动降级。");
        }

        return WppModeStatus.Disabled($"未检测到 WPP 策略,系统内部版本 {build}。");
    }

    /// <summary>当前环境是否禁止安装第三方打印驱动。</summary>
    public bool IsThirdPartyDriverInstallBlocked() => Detect().Enabled;
}

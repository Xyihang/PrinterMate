namespace PrinterMate.App.Startup;

/// <summary>
/// 程序标识与作者信息的【单一事实来源】。
/// 启动动画、启动协议、关于信息一律从这里取,避免同一句话在多处各写一遍而出现不一致。
/// </summary>
public static class AppIdentity
{
    public const string ProgramName = "PrinterMate 打印机工具";

    public const string Tagline = "打印机发现 · 驱动安装 · 测试打印";

    /// <summary>作者行(启动动画与协议都会显示这一行,改这里即可全局生效)。</summary>
    public const string AuthorLine = "Made By Xyihang and DS4.1";

    public const string CopyrightLine = "© 2026 Xyihang / DS4.1";
}

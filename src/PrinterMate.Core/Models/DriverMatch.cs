using PrinterMate.Catalog;

namespace PrinterMate.Core;

/// <summary>驱动供给层。</summary>
public enum DriverSource
{
    /// <summary>L0:系统免驱(Microsoft IPP Class Driver 等内置类驱动)。</summary>
    ClassDriver,

    /// <summary>L1:内置 UPD,离线安装。</summary>
    BuiltinUpd,

    /// <summary>L2:白名单官方源在线补齐。</summary>
    Online,

    /// <summary>没有可用方案。</summary>
    None,
}

/// <summary>一个可用的驱动候选。</summary>
public sealed record DriverCandidate(
    string DriverName,
    DriverSource Source,
    string Brand,
    string Family,
    PrintDescriptionLanguage? Pdl,
    string? InfPath,
    string? Sha256,
    MatchConfidence Confidence,
    string Reason,
    bool Installable,
    string? BlockReason)
{
    public string SourceText => Source switch
    {
        DriverSource.ClassDriver => "L0 系统免驱",
        DriverSource.BuiltinUpd => "L1 内置驱动",
        DriverSource.Online => "L2 在线源",
        _ => "无可用方案",
    };

    public string PdlText => Pdl switch
    {
        PrintDescriptionLanguage.ClassDriver => "Class Driver",
        PrintDescriptionLanguage.PCL6 => "PCL6",
        PrintDescriptionLanguage.PS => "PostScript",
        PrintDescriptionLanguage.GDI => "GDI",
        _ => "未知",
    };

    public string ConfidenceText => Confidence switch
    {
        MatchConfidence.High => "高",
        MatchConfidence.Medium => "中",
        _ => "低",
    };
}

/// <summary>匹配引擎的最终决策。</summary>
public sealed record DriverDecision(
    OperationStatus Status,
    string Layer,
    DriverCandidate? Selected,
    IReadOnlyList<DriverCandidate> Candidates,
    string Message,
    string Suggestion,
    bool RequiresDownload)
{
    public bool NeedsManualChoice => Candidates.Count > 1 && Selected is null;

    public static DriverDecision Unresolved(string message, string suggestion, IReadOnlyList<DriverCandidate>? candidates = null) =>
        new(OperationStatus.Failed, "-", null, candidates ?? Array.Empty<DriverCandidate>(), message, suggestion, false);
}

/// <summary>Windows Protected Print Mode 检测结果。</summary>
public sealed record WppModeStatus(bool Enabled, string Source, string Detail)
{
    public static WppModeStatus Disabled(string detail = "未检测到 Windows Protected Print Mode 策略。") => new(false, "-", detail);
}

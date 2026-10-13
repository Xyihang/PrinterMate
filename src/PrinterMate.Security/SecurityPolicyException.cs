namespace PrinterMate.Security;

/// <summary>
/// 安全策略拒绝异常。所有面向用户的消息均为简体中文,并说明拒绝原因与补救建议。
/// </summary>
public sealed class SecurityPolicyException : Exception
{
    public SecurityPolicyException(string reason, string? suggestion = null)
        : base(Compose(reason, suggestion))
    {
        Reason = reason;
        Suggestion = suggestion ?? string.Empty;
    }

    /// <summary>被拒绝的原因(中文)。</summary>
    public string Reason { get; }

    /// <summary>补救建议(中文),可能为空。</summary>
    public string Suggestion { get; }

    private static string Compose(string reason, string? suggestion)
        => string.IsNullOrWhiteSpace(suggestion) ? reason : $"{reason} 建议:{suggestion}";
}

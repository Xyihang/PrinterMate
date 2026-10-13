namespace PrinterMate.Core;

/// <summary>统一操作状态。UI 依据它决定提示颜色与后续动作。</summary>
public enum OperationStatus
{
    Success,

    /// <summary>失败,但用户可自行修复(例如未插纸)。</summary>
    Failed,

    /// <summary>需要用户先做一件事(例如同意提权、选择候选驱动)。</summary>
    NeedsUserAction,

    /// <summary>当前环境不支持该操作(例如 WPP 模式禁止安装第三方驱动)。</summary>
    NotSupported,

    /// <summary>权限不足。</summary>
    NotPermitted,

    Cancelled,
}

/// <summary>所有对外操作的统一返回值,禁止用异常直接冒泡到 UI。</summary>
public sealed record OperationResult<T>
{
    public required OperationStatus Status { get; init; }

    public T? Value { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>可操作建议,UI 必须展示。</summary>
    public string? Suggestion { get; init; }

    public Exception? Exception { get; init; }

    public bool IsSuccess => Status == OperationStatus.Success;

    public static OperationResult<T> Ok(T value, string message = "操作成功") => new()
    {
        Status = OperationStatus.Success,
        Value = value,
        Message = message,
    };

    public static OperationResult<T> Fail(string message, string? suggestion = null, OperationStatus status = OperationStatus.Failed, Exception? exception = null) => new()
    {
        Status = status,
        Message = message,
        Suggestion = suggestion,
        Exception = exception,
    };
}

/// <summary>把异常统一收敛成中文结果,避免任何未处理异常冒泡到 UI。</summary>
public static class OperationResultExtensions
{
    public static OperationResult<T> ToFailure<T>(this Exception ex, string action)
    {
        var message = string.IsNullOrWhiteSpace(ex.Message) ? $"{action}失败。" : ex.Message;
        var status = ex switch
        {
            UnauthorizedAccessException => OperationStatus.NotPermitted,
            PlatformNotSupportedException => OperationStatus.NotSupported,
            OperationCanceledException => OperationStatus.Cancelled,
            _ => OperationStatus.Failed,
        };

        var suggestion = status switch
        {
            OperationStatus.NotPermitted => "请点击「以管理员身份安装」重新执行该操作。",
            OperationStatus.NotSupported => "当前系统不支持该操作,请查看日志了解详情。",
            OperationStatus.Cancelled => "操作已取消。",
            _ => "请展开详情查看错误,或导出诊断报告后反馈。",
        };

        return new OperationResult<T>
        {
            Status = status,
            Message = message,
            Suggestion = suggestion,
            Exception = ex,
        };
    }

    public static OperationResult<TOut> Map<TIn, TOut>(this OperationResult<TIn> source, Func<TIn, TOut> map)
    {
        if (!source.IsSuccess || source.Value is null)
        {
            return new OperationResult<TOut>
            {
                Status = source.Status,
                Message = source.Message,
                Suggestion = source.Suggestion,
                Exception = source.Exception,
            };
        }

        return OperationResult<TOut>.Ok(map(source.Value), source.Message);
    }
}

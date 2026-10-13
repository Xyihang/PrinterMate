using PrinterMate.Core;

namespace PrinterMate.App.Services;

/// <summary>状态条的颜色级别。</summary>
public enum StatusLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record StatusUpdate(string Operation, string Result, string Suggestion, StatusLevel Level);

/// <summary>
/// 全局状态广播:任何页面(以及全局异常兜底)都可以把"当前操作 + 结果 + 建议"推到顶部状态条。
/// </summary>
public static class StatusHub
{
    public static event Action<StatusUpdate>? Updated;

    public static StatusUpdate? Last { get; private set; }

    public static void Report(string operation, string result, StatusLevel level, string suggestion = "")
    {
        var update = new StatusUpdate(
            operation ?? string.Empty,
            result ?? string.Empty,
            suggestion ?? string.Empty,
            level);

        Last = update;

        var handler = Updated;
        if (handler is null)
        {
            return;
        }

        Ui.Post(() => handler(update));
    }

    public static void Info(string operation, string result, string suggestion = "")
        => Report(operation, result, StatusLevel.Info, suggestion);

    public static void Success(string operation, string result, string suggestion = "")
        => Report(operation, result, StatusLevel.Success, suggestion);

    public static void Warn(string operation, string result, string suggestion = "")
        => Report(operation, result, StatusLevel.Warning, suggestion);

    public static void Error(string operation, string result, string suggestion = "")
        => Report(operation, result, StatusLevel.Error, suggestion);

    public static StatusLevel LevelOf(OperationStatus status) => status switch
    {
        OperationStatus.Success => StatusLevel.Success,
        OperationStatus.NeedsUserAction => StatusLevel.Warning,
        OperationStatus.NotSupported => StatusLevel.Warning,
        OperationStatus.NotPermitted => StatusLevel.Warning,
        OperationStatus.Cancelled => StatusLevel.Warning,
        _ => StatusLevel.Error,
    };
}

/// <summary>把 OperationResult 转成状态条文案,Message 与 Suggestion 都不允许丢失。</summary>
public static class ResultText
{
    public static string Message<T>(OperationResult<T> result)
        => string.IsNullOrWhiteSpace(result.Message) ? "操作已完成。" : result.Message;

    public static string Suggestion<T>(OperationResult<T> result)
        => string.IsNullOrWhiteSpace(result.Suggestion) ? string.Empty : result.Suggestion;

    public static void Publish<T>(string operation, OperationResult<T> result)
        => StatusHub.Report(operation, Message(result), StatusHub.LevelOf(result.Status), Suggestion(result));
}

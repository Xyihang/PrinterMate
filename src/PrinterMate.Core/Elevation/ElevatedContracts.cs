using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrinterMate.Core.Elevation;

/// <summary>提权辅助进程支持的动作。</summary>
public static class ElevatedActions
{
    public const string InstallPrinter = "install-printer";
    public const string RemovePrinter = "remove-printer";
    public const string RemoveDriver = "remove-driver";
}

/// <summary>端口的可序列化描述(提权进程与主程序共用)。</summary>
public sealed record ElevatedPortRequest(
    string Kind,
    string PortName,
    string? HostAddress = null,
    int? PortNumber = null,
    string? SharePath = null);

/// <summary>提权安装请求。写入临时文件后由辅助进程读取。</summary>
public sealed record ElevatedInstallRequest(
    string RequestId,
    string Action,
    string QueueName,
    string DriverName,
    string? InfPath,
    string? ExpectedSha256,
    string? ExpectedSignerSubject,
    string? Brand,
    string? SourceUrl,
    ElevatedPortRequest Port,
    DateTimeOffset IssuedAt)
{
    public string? Target => IsRemoteShare ? Port.SharePath : QueueName;

    [JsonIgnore]
    public bool IsRemoteShare => string.Equals(Port.Kind, "Shared", StringComparison.OrdinalIgnoreCase);
}

/// <summary>单步执行记录。</summary>
public sealed record ElevatedStepRecord(
    string Step,
    string State,
    string Message,
    string Command,
    int ExitCode,
    string Output,
    double DurationMs);

/// <summary>提权安装结果。</summary>
public sealed record ElevatedInstallResponse(
    string RequestId,
    bool Success,
    string Summary,
    IReadOnlyList<ElevatedStepRecord> Steps,
    string Error)
{
    public static ElevatedInstallResponse Failure(string requestId, string error, IReadOnlyList<ElevatedStepRecord>? steps = null)
        => new(requestId, false, error, steps ?? Array.Empty<ElevatedStepRecord>(), error);
}

/// <summary>请求/响应共用的 JSON 序列化设置。</summary>
public static class ElevatedJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? DeserializeFile<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception)
        {
            return default;
        }
    }
}

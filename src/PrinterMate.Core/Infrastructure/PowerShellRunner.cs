using System.Text.Json;

namespace PrinterMate.Core.Infrastructure;

/// <summary>
/// PowerShell 执行器。所有 PrintManagement / PnP 查询都经由此处,
/// 统一使用 UTF-8 输出并把结果以 JSON 形式回传,避免解析本地化文本。
/// </summary>
public sealed class PowerShellRunner
{
    private const string Utf8Preamble = "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);";

    public static string? ExecutablePath { get; } = ResolveExecutable();

    public static bool IsAvailable => ExecutablePath is not null;

    private readonly TimeSpan _defaultTimeout;

    public PowerShellRunner(TimeSpan? defaultTimeout = null)
    {
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>执行脚本并原样返回结果(文本模式)。</summary>
    public ProcessResult RunRaw(string body, TimeSpan? timeout = null)
        => RunScript(body, asJson: false, timeout);

    /// <summary>执行脚本并把输出解析为 JSON 元素数组。</summary>
    public OperationResult<IReadOnlyList<JsonElement>> RunJson(string body, TimeSpan? timeout = null)
    {
        var result = RunScript(body, asJson: true, timeout);
        if (result.TimedOut)
        {
            return OperationResult<IReadOnlyList<JsonElement>>.Fail(
                "PowerShell 命令执行超时。",
                "请检查系统资源占用或后台打印服务(Spooler)是否响应。",
                OperationStatus.Failed);
        }

        if (result.ExitCode != 0)
        {
            return OperationResult<IReadOnlyList<JsonElement>>.Fail(
                string.IsNullOrWhiteSpace(result.StandardError) ? "PowerShell 命令执行失败。" : result.StandardError.Trim(),
                "请确认 PrintManagement 模块可用,且当前账户有权限读取打印配置。");
        }

        var text = result.StandardOutput.Trim();
        if (text.Length == 0)
        {
            return OperationResult<IReadOnlyList<JsonElement>>.Ok(Array.Empty<JsonElement>());
        }

        try
        {
            // 单个对象时 ConvertTo-Json 输出对象而非数组,这里统一包成数组。
            if (text.StartsWith('{'))
            {
                text = "[" + text + "]";
            }

            using var doc = JsonDocument.Parse(text);
            var list = new List<JsonElement>(doc.RootElement.GetArrayLength());
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                list.Add(item.Clone());
            }

            return OperationResult<IReadOnlyList<JsonElement>>.Ok(list);
        }
        catch (JsonException ex)
        {
            return OperationResult<IReadOnlyList<JsonElement>>.Fail(
                $"PowerShell 返回内容无法解析为 JSON:{ex.Message}",
                "请把该命令的输出通过「导出诊断报告」反馈,以便定位。");
        }
    }

    public ProcessResult RunScript(string body, bool asJson, TimeSpan? timeout = null)
    {
        var exe = ExecutablePath;
        if (exe is null)
        {
            return new ProcessResult("powershell", body, -1, string.Empty, "系统未找到 PowerShell(pwsh 或 powershell.exe)。", TimeSpan.Zero, false);
        }

        var inner = asJson
            ? "$__r | ConvertTo-Json -Depth 6 -Compress"
            : "$__r | Out-String -Width 4096";

        var script = Utf8Preamble
            + "$__r = @( & {" + Environment.NewLine
            + body + Environment.NewLine
            + "} );" + Environment.NewLine
            + inner + Environment.NewLine
            + "if ($__r -eq $null) { exit 0 }";

        var wrapped = Utf8Preamble
            + "try { " + script + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";

        var args = new List<string> { "-NoProfile", "-NonInteractive", "-NoLogo", "-ExecutionPolicy", "Bypass", "-Command", wrapped };
        return ProcessRunner.Run(exe, args, timeout ?? _defaultTimeout);
    }

    private static string? ResolveExecutable()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pwsh = Path.Combine(pf, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh))
        {
            return pwsh;
        }

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var ps = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(ps) ? ps : null;
    }
}

/// <summary>从 PowerShell 输出的 JSON 对象里安全取字段。</summary>
public static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    public static string GetString(this JsonElement element, string property, string fallback = "")
        => element.GetStringOrNull(property) ?? fallback;

    public static int GetInt(this JsonElement element, string property, int fallback = 0)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    public static bool GetBool(this JsonElement element, string property, bool fallback = false)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    public static IReadOnlyList<string> GetStringArray(this JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return Array.Empty<string>();
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString() ?? string.Empty)
                .Where(v => v.Length > 0)
                .ToList(),
            JsonValueKind.String => new[] { value.GetString() ?? string.Empty },
            _ => Array.Empty<string>(),
        };
    }
}

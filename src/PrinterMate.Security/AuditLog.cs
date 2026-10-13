using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PrinterMate.Security;

/// <summary>一条审计记录。</summary>
public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string Operation,
    string Target,
    string Outcome,
    string SourceUrl,
    string Sha256,
    string SignerSubject,
    string Detail,
    string User);

/// <summary>
/// 审计日志:按天写入 <c>audit-YYYYMMDD.jsonl</c>,UTF-8 编码,一行一条 JSON,便于追加与流式解析。
/// </summary>
public sealed class AuditLog
{
    private const string FilePrefix = "audit-";
    private const string FileSuffix = ".jsonl";

    private readonly object _gate = new();

    public AuditLog(string? directory = null)
    {
        Directory = string.IsNullOrWhiteSpace(directory)
            ? GetDefaultDirectory()
            : Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(Directory);
    }

    /// <summary>审计日志目录。</summary>
    public string Directory { get; }

    /// <summary>当前日期的日志文件完整路径。</summary>
    public string CurrentFile => Path.Combine(Directory, FileName(DateOnly.FromDateTime(DateTime.Now)));

    /// <summary>写入一条审计记录。</summary>
    public void Write(AuditEntry entry, bool flush = true)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = entry with
        {
            Operation = entry.Operation ?? string.Empty,
            Target = entry.Target ?? string.Empty,
            Outcome = entry.Outcome ?? string.Empty,
            SourceUrl = entry.SourceUrl ?? string.Empty,
            Sha256 = entry.Sha256 ?? string.Empty,
            SignerSubject = entry.SignerSubject ?? string.Empty,
            Detail = entry.Detail ?? string.Empty,
            User = string.IsNullOrWhiteSpace(entry.User) ? CurrentUser() : entry.User,
        };

        var line = JsonSerializer.Serialize(normalized, JsonOptions);
        var path = Path.Combine(Directory, FileName(DateOnly.FromDateTime(entry.Timestamp.LocalDateTime)));

        lock (_gate)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(line);
            if (flush)
            {
                writer.Flush();
                stream.Flush(true);
            }
        }
    }

    /// <summary>按字段写入一条审计记录(时间戳取当前时间,用户取当前登录用户)。</summary>
    public void Write(
        string operation,
        string target,
        string outcome,
        string detail,
        string sourceUrl = "",
        string sha256 = "",
        string signerSubject = "")
    {
        Write(new AuditEntry(
            DateTimeOffset.Now,
            operation,
            target,
            outcome,
            sourceUrl,
            sha256,
            signerSubject,
            detail,
            CurrentUser()));
    }

    /// <summary>读取指定日期的审计记录。文件不存在或某行损坏时不会抛异常。</summary>
    public IReadOnlyList<AuditEntry> Read(DateOnly day)
    {
        var path = Path.Combine(Directory, FileName(day));
        var result = new List<AuditEntry>();

        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return result;
            }

            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<AuditEntry>(line, JsonOptions);
                    if (entry is not null)
                    {
                        result.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // 跳过损坏的行,避免单行异常影响整体审计读取。
                }
            }
        }

        result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return result;
    }

    /// <summary>把指定日期区间的审计记录导出为 JSONL 文件,返回导出文件的完整路径。</summary>
    public string Export(string destinationPath, DateOnly? from = null, DateOnly? to = null)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("导出路径为空,无法导出审计日志。", nameof(destinationPath));
        }

        var fullPath = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            System.IO.Directory.CreateDirectory(parent);
        }

        var start = from ?? DateOnly.MinValue;
        var end = to ?? DateOnly.MaxValue;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var entries = new List<AuditEntry>();
        lock (_gate)
        {
            if (System.IO.Directory.Exists(Directory))
            {
                foreach (var file in System.IO.Directory.GetFiles(Directory, FilePrefix + "*" + FileSuffix))
                {
                    if (!TryParseFileName(Path.GetFileName(file), out var day) || day < start || day > end)
                    {
                        continue;
                    }

                    foreach (var line in File.ReadLines(file, Encoding.UTF8))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        try
                        {
                            var entry = JsonSerializer.Deserialize<AuditEntry>(line, JsonOptions);
                            if (entry is not null)
                            {
                                entries.Add(entry);
                            }
                        }
                        catch (JsonException)
                        {
                        }
                    }
                }
            }
        }

        entries.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.AppendLine(JsonSerializer.Serialize(entry, JsonOptions));
        }

        File.WriteAllText(fullPath, builder.ToString(), new UTF8Encoding(false));
        return fullPath;
    }

    private static bool TryParseFileName(string fileName, out DateOnly day)
    {
        day = default;
        if (!fileName.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var raw = fileName[FilePrefix.Length..^FileSuffix.Length];
        return DateOnly.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    private static string FileName(DateOnly day) => $"{FilePrefix}{day:yyyyMMdd}{FileSuffix}";

    private static string GetDefaultDirectory()
    {
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "PrinterMate", "logs");
    }

    private static string CurrentUser()
    {
        try
        {
            return System.Environment.UserName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };
}

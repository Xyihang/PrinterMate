using System.Text.Json;

namespace PrinterMate.Security;

/// <summary>信任账本中的一条记录(首次见到某文件的哈希并固定下来)。</summary>
public sealed record TrustLedgerEntry(
    string Key,
    string Sha256,
    string SignerSubject,
    DateTimeOffset FirstSeen,
    string SourceUrl);

/// <summary>
/// 信任账本:基于 TOFU(首次使用即信任)策略固定文件哈希。
/// 同一键再次出现但哈希不同,说明文件被替换,必须拒绝。
/// </summary>
public sealed class TrustLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TrustLedgerEntry> _entries;

    public TrustLedger(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultPath()
            : Path.GetFullPath(filePath);
        _entries = Load(FilePath);
    }

    /// <summary>账本文件完整路径。</summary>
    public string FilePath { get; }

    /// <summary>尝试读取指定键的记录。</summary>
    public bool TryGet(string key, out TrustLedgerEntry entry)
    {
        if (key is null)
        {
            entry = null!;
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var found))
            {
                entry = found;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    /// <summary>固定一条记录并落盘。</summary>
    public void Pin(TrustLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Key))
        {
            throw new ArgumentException("信任账本的键不能为空。", nameof(entry));
        }

        lock (_gate)
        {
            _entries[entry.Key] = entry;
            Save();
        }
    }

    /// <summary>
    /// 校验并固定:首次见到则记录并返回 true;已有记录且哈希相同返回 true;
    /// 已有记录但哈希不同返回 false(文件被替换,必须拒绝)。
    /// </summary>
    public bool VerifyOrPin(string key, string sha256, string signerSubject, string sourceUrl, out string reason)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            reason = "信任账本的键为空,无法校验文件来源。";
            return false;
        }

        if (!Sha256Verifier.IsValidHex(sha256))
        {
            reason = "待固定文件的 SHA256 不是合法的 64 位十六进制值,无法记账。";
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (Sha256Verifier.Equals(existing.Sha256, sha256))
                {
                    reason = $"哈希与信任账本记录一致,文件未被替换(首次记录于 {existing.FirstSeen:yyyy-MM-dd HH:mm:ss})。";
                    return true;
                }

                reason =
                    $"哈希与信任账本记录不一致:文件可能已被替换或篡改,已拒绝。"
                    + $"(首次记录 {existing.FirstSeen:yyyy-MM-dd HH:mm:ss},记录哈希 {existing.Sha256},当前哈希 {sha256})";
                return false;
            }

            _entries[key] = new TrustLedgerEntry(key, sha256, signerSubject ?? string.Empty, DateTimeOffset.Now, sourceUrl ?? string.Empty);
            Save();
            reason = "首次见到该文件,已把哈希固定到信任账本。";
            return true;
        }
    }

    private void Save()
    {
        var parent = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(parent))
        {
            System.IO.Directory.CreateDirectory(parent);
        }

        var payload = _entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);
    }

    private static Dictionary<string, TrustLedgerEntry> Load(string path)
    {
        var map = new Dictionary<string, TrustLedgerEntry>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return map;
        }

        try
        {
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<TrustLedgerEntry>>(json, JsonOptions);
            if (list is not null)
            {
                foreach (var entry in list)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Key))
                    {
                        map[entry.Key] = entry;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 账本损坏时回退为空账本,后续会重新固定哈希。
        }

        return map;
    }

    private static string GetDefaultPath()
    {
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "PrinterMate", "trust-ledger.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };
}

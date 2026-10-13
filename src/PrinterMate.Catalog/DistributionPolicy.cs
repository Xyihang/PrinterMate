using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrinterMate.Catalog;

/// <summary>本机驱动分发策略的持久化结构。</summary>
public sealed class DistributionPolicyFile
{
    [JsonPropertyName("all")]
    public bool All { get; set; }

    [JsonPropertyName("acknowledgement")]
    public string Acknowledgement { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("brands")]
    public Dictionary<string, bool> Brands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 本机驱动分发策略。
///
/// 设计边界(很重要):
///   - 随包分发的 <c>Catalog/drivers.json</c> 里的 <c>redistributable</c> 是**发行方**的红线,默认 false;
///   - 本类代表**本机使用者**的声明:如果你只是自己用、不对外分发软件,
///     你可以在本机开启某品牌(或全部品牌)的内置驱动,这不修改受审计的清单文件;
///   - 无论开关如何,驱动包**仍然必须**通过 SHA256 与 .cat 数字签名校验,
///     这个开关只影响"是否允许从本地驱动库安装",不影响任何安全校验。
/// </summary>
public sealed class DistributionPolicy
{
    private const string FileName = "distribution-policy.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Lazy<DistributionPolicy> LazyDefault = new(() => new DistributionPolicy());

    private readonly object _gate = new();
    private DistributionPolicyFile _data;

    public DistributionPolicy(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrinterMate",
            FileName);

        _data = Load();
    }

    /// <summary>默认策略文件:%LOCALAPPDATA%\PrinterMate\distribution-policy.json</summary>
    public static DistributionPolicy Default => LazyDefault.Value;

    public string FilePath { get; }

    /// <summary>是否已开启全部品牌的内置驱动。</summary>
    public bool AllowsAll
    {
        get
        {
            lock (_gate)
            {
                return _data.All;
            }
        }
    }

    /// <summary>使用者在开启时填写的声明文本(用于审计)。</summary>
    public string Acknowledgement
    {
        get
        {
            lock (_gate)
            {
                return _data.Acknowledgement;
            }
        }
    }

    public DateTimeOffset? UpdatedAt
    {
        get
        {
            lock (_gate)
            {
                return _data.UpdatedAt;
            }
        }
    }

    public IReadOnlyDictionary<string, bool> BrandOverrides
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, bool>(_data.Brands, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>某个品牌的内置驱动是否允许在本机安装。</summary>
    public bool IsEnabled(string brand)
    {
        if (string.IsNullOrWhiteSpace(brand))
        {
            return false;
        }

        lock (_gate)
        {
            if (_data.Brands.TryGetValue(brand, out var value))
            {
                return value;
            }

            return _data.All;
        }
    }

    /// <summary>开启/关闭全部品牌。</summary>
    public void SetAll(bool enabled, string acknowledgement)
    {
        lock (_gate)
        {
            _data.All = enabled;
            _data.Acknowledgement = enabled ? acknowledgement : string.Empty;
            _data.UpdatedAt = DateTimeOffset.Now;
            Save();
        }
    }

    /// <summary>开启/关闭单个品牌。会覆盖全局设置。</summary>
    public void SetBrand(string brand, bool enabled, string acknowledgement)
    {
        if (string.IsNullOrWhiteSpace(brand))
        {
            return;
        }

        lock (_gate)
        {
            _data.Brands[brand] = enabled;
            if (enabled)
            {
                _data.Acknowledgement = acknowledgement;
            }

            _data.UpdatedAt = DateTimeOffset.Now;
            Save();
        }
    }

    /// <summary>清除某个品牌的单独设置,回落到全局开关。</summary>
    public void ResetBrand(string brand)
    {
        lock (_gate)
        {
            if (_data.Brands.Remove(brand))
            {
                _data.UpdatedAt = DateTimeOffset.Now;
                Save();
            }
        }
    }

    /// <summary>汇总当前策略,用于界面与诊断报告展示。</summary>
    public string Describe()
    {
        lock (_gate)
        {
            var enabled = _data.Brands.Where(b => b.Value).Select(b => b.Key).OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
            var disabled = _data.Brands.Where(b => !b.Value).Select(b => b.Key).OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();

            var parts = new List<string>
            {
                _data.All ? "全部品牌:已开启内置驱动" : "全部品牌:默认关闭(仅使用在线源)",
            };

            if (enabled.Count > 0)
            {
                parts.Add($"单独开启:{string.Join(", ", enabled)}");
            }

            if (disabled.Count > 0)
            {
                parts.Add($"单独关闭:{string.Join(", ", disabled)}");
            }

            if (!string.IsNullOrWhiteSpace(_data.Acknowledgement))
            {
                parts.Add($"声明:{_data.Acknowledgement}");
            }

            return string.Join(";", parts);
        }
    }

    private DistributionPolicyFile Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<DistributionPolicyFile>(File.ReadAllText(FilePath), Options)
                       ?? new DistributionPolicyFile();
            }
        }
        catch (Exception)
        {
            // 策略文件损坏时回落到"全部关闭",即安全默认值。
        }

        return new DistributionPolicyFile();
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, Options));
        }
        catch (Exception)
        {
            // 写入失败不影响本次会话内的策略生效。
        }
    }
}

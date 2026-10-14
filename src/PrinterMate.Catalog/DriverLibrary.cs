using System.Reflection;
using System.Text.Json;

namespace PrinterMate.Catalog;

/// <summary>某条驱动在指定架构下是否可安装,以及不可安装的原因。</summary>
public sealed record DriverAvailability(bool Available, string Reason, string? InfPath, string? Sha256)
{
    public static DriverAvailability No(string reason) => new(false, reason, null, null);
}

/// <summary>驱动库整体状态,用于 UI 展示"自带驱动是否就绪"。</summary>
public sealed record DriverLibraryStatus(
    string Root,
    string ManifestPath,
    string LibraryVersion,
    bool UsingEmbeddedManifest,
    int TotalEntries,
    int InboxEntries,
    int PackagedEntries,
    int ReadyEntries,
    IReadOnlyList<string> Brands,
    IReadOnlyList<string> MissingPackages,
    string DistributionSummary)
{
    public bool HasAnyPackagedDriver => ReadyEntries > 0;
}

/// <summary>
/// 内置通用驱动(UPD)库。负责加载 drivers.json、解析驱动包在磁盘上的实际位置,
/// 并把"授权 / 架构 / 哈希是否齐备"转成可安装性判定。
/// </summary>
public sealed class DriverLibrary
{
    public const string ManifestRelativePath = @"Catalog\drivers.json";
    private const string EmbeddedManifestName = "PrinterMate.Catalog.Data.drivers.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly bool _mergeWithBaseline;

    private DriverLibrary(
        string root,
        string manifestPath,
        DriverManifest manifest,
        bool embedded,
        DistributionPolicy policy,
        bool mergeWithBaseline)
    {
        Root = root;
        ManifestPath = manifestPath;
        Manifest = manifest;
        UsingEmbeddedManifest = embedded;
        Policy = policy;
        _mergeWithBaseline = mergeWithBaseline;
        Entries = manifest.Entries
            .OrderByDescending(e => e.Priority)
            .ThenBy(e => e.Brand, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>驱动库根目录(其下应有 Drivers\ 与 Catalog\ 子目录)。</summary>
    public string Root { get; }

    public string ManifestPath { get; private set; }

    public DriverManifest Manifest { get; private set; }

    public bool UsingEmbeddedManifest { get; private set; }

    /// <summary>本机驱动分发策略(清单红线之上的本机声明)。</summary>
    public DistributionPolicy Policy { get; }

    public IReadOnlyList<DriverCatalogEntry> Entries { get; private set; }

    /// <summary>重新从磁盘加载清单(写入驱动库或更换策略后刷新视图用)。保持加载模式不变。</summary>
    public void Reload()
    {
        var reloaded = Load(Root, Policy, _mergeWithBaseline);
        ManifestPath = reloaded.ManifestPath;
        Manifest = reloaded.Manifest;
        UsingEmbeddedManifest = reloaded.UsingEmbeddedManifest;
        Entries = reloaded.Entries;
    }

    /// <param name="mergeWithBaseline">
    /// true(默认,生产行为):磁盘清单与随包清单合并,磁盘按「品牌+PDL」覆盖同键条目 ——
    /// 这样程序升级后新增的品牌/型号一定能生效,同时保留用户导入的驱动包。
    /// false:只用磁盘清单,把它当作完整世界。适用于企业锁定私有清单的场景,以及单测。
    /// </param>
    public static DriverLibrary Load(
        string? rootOverride = null,
        DistributionPolicy? policy = null,
        bool mergeWithBaseline = true)
    {
        policy ??= DistributionPolicy.Default;
        var root = rootOverride ?? ResolveDefaultRoot();

        var candidates = new[]
        {
            Path.Combine(root, ManifestRelativePath),
            Path.Combine(root, "drivers.json"),
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var manifest = JsonSerializer.Deserialize<DriverManifest>(File.ReadAllText(candidate), Options);
                if (manifest is { Entries.Count: > 0 })
                {
                    if (!mergeWithBaseline)
                    {
                        return new DriverLibrary(root, candidate, manifest, embedded: false, policy, mergeWithBaseline);
                    }

                    // 关键:磁盘清单必须与随包清单【合并】,不能直接覆盖。
                    // 早期实现是"磁盘有清单就整个替换",导致本机一旦存在旧版清单,
                    // 后续版本新增的品牌/型号永远不生效
                    // (实测:随包 50 条,本机旧清单 22 条,新加的 12 个品牌全部不可见)。
                    var baseline = LoadEmbedded();
                    var merged = MergeManifests(baseline, manifest);
                    return new DriverLibrary(root, candidate, merged, embedded: false, policy, mergeWithBaseline);
                }
            }
            catch (JsonException)
            {
                // 磁盘清单损坏时回落到内置清单,保证程序仍可用。
            }
        }

        var embeddedManifest = LoadEmbedded();
        return new DriverLibrary(root, $"(内置资源) {EmbeddedManifestName}", embeddedManifest, embedded: true, policy, mergeWithBaseline);
    }

    /// <summary>
    /// 合并随包清单与本机清单。
    ///
    /// 规则:
    ///   · 以随包清单为【基线】—— 保证程序升级后新增的品牌/型号一定能出现;
    ///   · 本机清单按「品牌 + PDL」覆盖同键条目 —— 保证用户导入的驱动包、SHA256 与标记不丢。
    /// </summary>
    private static DriverManifest MergeManifests(DriverManifest baseline, DriverManifest local)
    {
        static string KeyOf(DriverCatalogEntry e) => $"{e.Brand}|{e.Pdl}";

        var localKeys = new HashSet<string>(local.Entries.Select(KeyOf), StringComparer.OrdinalIgnoreCase);

        var merged = new DriverManifest
        {
            SchemaVersion = string.IsNullOrWhiteSpace(local.SchemaVersion) ? baseline.SchemaVersion : local.SchemaVersion,
            LibraryVersion = string.IsNullOrWhiteSpace(local.LibraryVersion)
                ? baseline.LibraryVersion
                : $"{baseline.LibraryVersion}+local",
            Notes = baseline.Notes,
        };

        merged.Entries.AddRange(baseline.Entries.Where(e => !localKeys.Contains(KeyOf(e))));
        merged.Entries.AddRange(local.Entries);

        return merged;
    }

    /// <summary>按优先级查找默认根目录:环境变量 → 用户目录(在线更新) → 程序目录 → 开发目录。</summary>
    public static string ResolveDefaultRoot()
    {
        var env = Environment.GetEnvironmentVariable("PRINTERMATE_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return env;
        }

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrinterMate");
        if (File.Exists(Path.Combine(local, ManifestRelativePath)))
        {
            return local;
        }

        var baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, ManifestRelativePath)))
        {
            return baseDir;
        }

        // 开发/测试场景:从程序目录逐级上溯寻找 Catalog\drivers.json
        var probe = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && probe is not null; i++, probe = probe.Parent)
        {
            if (File.Exists(Path.Combine(probe.FullName, ManifestRelativePath)))
            {
                return probe.FullName;
            }
        }

        return baseDir;
    }

    public IReadOnlyList<DriverCatalogEntry> ForBrand(string brand)
        => Entries.Where(e => string.Equals(e.Brand, brand, StringComparison.OrdinalIgnoreCase)).ToList();

    public DriverCatalogEntry? FindByDriverName(string driverName)
        => Entries.FirstOrDefault(e => string.Equals(e.DriverName, driverName, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> Brands
        => Entries.Select(e => e.Brand).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>该条目在本机是否允许从内置驱动库安装(清单红线 或 本机声明)。</summary>
    public bool IsDistributionAllowed(DriverCatalogEntry entry)
        => entry.Redistributable || Policy.IsEnabled(entry.Brand);

    /// <summary>开启/关闭某品牌在本机的内置驱动安装许可,并记录使用者声明。</summary>
    public void SetBrandDistribution(string brand, bool enabled, string acknowledgement)
        => Policy.SetBrand(brand, enabled, acknowledgement);

    /// <summary>判断某条驱动在当前架构下能否直接本地安装。</summary>
    public DriverAvailability CheckAvailability(DriverCatalogEntry entry, string arch, string? os = null)
    {
        if (entry.IsInboxDriver)
        {
            return new DriverAvailability(true, "系统内置类驱动,无需安装文件。", null, null);
        }

        if (!entry.Arch.Any(a => string.Equals(a, arch, StringComparison.OrdinalIgnoreCase)))
        {
            return DriverAvailability.No($"该驱动不支持当前系统架构({arch})。");
        }

        if (os is not null && entry.Os.Count > 0 && !entry.Os.Any(o => os.StartsWith(o, StringComparison.OrdinalIgnoreCase)))
        {
            return DriverAvailability.No($"该驱动不适用于当前系统版本({os})。");
        }

        if (!IsDistributionAllowed(entry))
        {
            return DriverAvailability.No(
                $"品牌 {entry.Brand} 的驱动再分发授权尚未确认(清单 redistributable=false),程序禁止从本地安装,将改用官方在线源。"
                + "若你仅在本机个人使用、不对外分发本软件,可在「驱动管理」页开启该品牌的内置驱动。");
        }

        var inf = ResolveInfPath(entry);
        if (inf is null)
        {
            return DriverAvailability.No($"驱动库中缺少 {entry.Brand} {entry.Family} 的驱动文件(期望路径:{entry.InfRelPath})。");
        }

        var sha = entry.Sha256.For(arch);
        if (string.IsNullOrWhiteSpace(sha))
        {
            return DriverAvailability.No($"驱动库清单缺少 {entry.Brand} {entry.Family} 在 {arch} 架构下的 SHA256,拒绝安装。");
        }

        return new DriverAvailability(true, "内置驱动已就绪,可离线安装。", inf, sha);
    }

    /// <summary>把 infRelPath(可含通配符)解析成磁盘上的绝对路径;找不到返回 null。</summary>
    public string? ResolveInfPath(DriverCatalogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.InfRelPath))
        {
            return null;
        }

        var relative = entry.InfRelPath.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        var dir = Path.GetDirectoryName(full);
        var pattern = Path.GetFileName(full);

        if (dir is null || !Directory.Exists(dir))
        {
            return null;
        }

        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            return File.Exists(full) ? full : null;
        }

        return Directory.EnumerateFiles(dir, pattern)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public DriverLibraryStatus GetStatus(string arch)
    {
        var ready = 0;
        var packaged = 0;
        var inbox = 0;
        var missing = new List<string>();

        foreach (var entry in Entries)
        {
            if (entry.IsInboxDriver)
            {
                inbox++;
                ready++;
                continue;
            }

            packaged++;
            var availability = CheckAvailability(entry, arch);
            if (availability.Available)
            {
                ready++;
            }
            else
            {
                missing.Add($"{entry.Brand} {entry.Family} [{entry.Pdl}] — {availability.Reason}");
            }
        }

        return new DriverLibraryStatus(
            Root,
            ManifestPath,
            Manifest.LibraryVersion,
            UsingEmbeddedManifest,
            Entries.Count,
            inbox,
            packaged,
            ready,
            Brands,
            missing,
            Policy.Describe());
    }

    private static DriverManifest LoadEmbedded()
    {
        var assembly = typeof(DriverLibrary).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedManifestName);
        if (stream is null)
        {
            var available = string.Join(", ", assembly.GetManifestResourceNames());
            throw new InvalidOperationException($"未能加载内置驱动清单资源 {EmbeddedManifestName}。现有资源:{available}");
        }

        var manifest = JsonSerializer.Deserialize<DriverManifest>(stream, Options);
        return manifest ?? throw new InvalidOperationException("内置驱动清单解析失败。");
    }
}

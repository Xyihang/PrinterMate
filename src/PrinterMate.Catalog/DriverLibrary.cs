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
    IReadOnlyList<string> MissingPackages)
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

    private DriverLibrary(string root, string manifestPath, DriverManifest manifest, bool embedded)
    {
        Root = root;
        ManifestPath = manifestPath;
        Manifest = manifest;
        UsingEmbeddedManifest = embedded;
        Entries = manifest.Entries
            .OrderByDescending(e => e.Priority)
            .ThenBy(e => e.Brand, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>驱动库根目录(其下应有 Drivers\ 与 Catalog\ 子目录)。</summary>
    public string Root { get; }

    public string ManifestPath { get; }

    public DriverManifest Manifest { get; }

    public bool UsingEmbeddedManifest { get; }

    public IReadOnlyList<DriverCatalogEntry> Entries { get; }

    public static DriverLibrary Load(string? rootOverride = null)
    {
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
                    return new DriverLibrary(root, candidate, manifest, embedded: false);
                }
            }
            catch (JsonException)
            {
                // 磁盘清单损坏时回落到内置清单,保证程序仍可用。
            }
        }

        var embeddedManifest = LoadEmbedded();
        return new DriverLibrary(root, $"(内置资源) {EmbeddedManifestName}", embeddedManifest, embedded: true);
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

        if (!entry.Redistributable)
        {
            return DriverAvailability.No($"品牌 {entry.Brand} 的驱动再分发授权尚未确认,程序禁止从本地安装,将改用官方在线源。");
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
            missing);
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

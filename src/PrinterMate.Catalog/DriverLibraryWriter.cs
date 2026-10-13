using System.Text.Json;

namespace PrinterMate.Catalog;

/// <summary>把已下载/已校验的驱动包收进本机驱动库的请求。</summary>
public sealed record DriverPackageInstallRequest(
    string Brand,
    string Family,
    PrintDescriptionLanguage Pdl,
    string DriverName,
    string SourceDirectory,
    string InfFileName,
    string Sha256,
    string SignerSubject,
    IReadOnlyList<string> Arch,
    bool ConfirmRedistribution,
    string Version = "",
    IReadOnlyList<string>? ModelPatterns = null,
    IReadOnlyList<string>? HardwareIdPatterns = null,
    int Priority = 100);

/// <summary>写入结果(不依赖 Core,避免反向引用)。</summary>
public sealed record DriverLibraryWriteResult(
    bool Success,
    string Message,
    string? ManifestPath,
    string? InfRelativePath,
    DriverCatalogEntry? Entry)
{
    public static DriverLibraryWriteResult Fail(string message) => new(false, message, null, null, null);
}

/// <summary>
/// 本机驱动库写入器:把驱动包复制到 <c>Drivers/&lt;Brand&gt;/&lt;slug&gt;/</c>,
/// 并把条目回填进磁盘清单 <c>Catalog/drivers.json</c>。
///
/// 磁盘清单不存在时会先用程序当前使用的清单(通常是随包的内置资源)播种。
/// 写入只发生在 <see cref="DriverLibrary.Root"/> 之下,不会碰随包发布的原始清单文件。
/// </summary>
public sealed class DriverLibraryWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly DriverLibrary _library;

    public DriverLibraryWriter(DriverLibrary library)
    {
        _library = library;
    }

    public DriverLibraryWriteResult AddOrUpdate(DriverPackageInstallRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Brand) || string.IsNullOrWhiteSpace(request.Family))
        {
            return DriverLibraryWriteResult.Fail("品牌与驱动家族不能为空。");
        }

        if (string.IsNullOrWhiteSpace(request.DriverName))
        {
            return DriverLibraryWriteResult.Fail("驱动名称不能为空(必须与 INF 的 [Models] 段一致)。");
        }

        if (!Directory.Exists(request.SourceDirectory))
        {
            return DriverLibraryWriteResult.Fail($"源目录不存在:{request.SourceDirectory}");
        }

        if (!File.Exists(Path.Combine(request.SourceDirectory, request.InfFileName)))
        {
            return DriverLibraryWriteResult.Fail($"源目录中找不到 INF:{request.InfFileName}");
        }

        try
        {
            var slug = Slugify($"{request.Family}-{request.Pdl}".ToLowerInvariant());
            var targetDirectory = Path.Combine(_library.Root, "Drivers", SanitizeSegment(request.Brand), slug);
            Directory.CreateDirectory(targetDirectory);
            CopyDirectory(request.SourceDirectory, targetDirectory);

            var targetInf = Path.Combine(targetDirectory, request.InfFileName);
            var infRelative = Path.GetRelativePath(_library.Root, targetInf).Replace('\\', '/');

            var manifestPath = EnsureDiskManifest();
            if (manifestPath is null)
            {
                return DriverLibraryWriteResult.Fail($"无法在 {_library.Root} 下创建或读取清单文件。");
            }

            var manifest = ReadManifest(manifestPath) ?? new DriverManifest();
            var existing = manifest.Entries.FirstOrDefault(e =>
                string.Equals(e.Brand, request.Brand, StringComparison.OrdinalIgnoreCase)
                && e.Pdl == request.Pdl);

            var patterns = request.ModelPatterns is { Count: > 0 }
                ? request.ModelPatterns
                : existing?.ModelPatterns ?? new[] { "*" };

            var hardwareIds = request.HardwareIdPatterns is { Count: > 0 }
                ? request.HardwareIdPatterns
                : existing?.HardwareIdPatterns ?? new[] { $"USBPRINT\\{request.Brand}*", $"WSDPRINT\\*{request.Brand}*" };

            var hashes = existing?.Sha256 ?? new DriverPackageHashes();
            var arch = request.Arch.Count > 0 ? request.Arch : new[] { "x64" };
            var newHashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in arch)
            {
                newHashes[a] = request.Sha256;
            }

            var entry = new DriverCatalogEntry
            {
                Brand = request.Brand,
                Family = request.Family,
                Pdl = request.Pdl,
                DriverName = request.DriverName,
                InfRelPath = infRelative,
                Arch = arch,
                Os = existing?.Os is { Count: > 0 } ? existing.Os : new[] { "win10", "win11" },
                Version = string.IsNullOrWhiteSpace(request.Version) ? existing?.Version ?? string.Empty : request.Version,
                SignerSubject = string.IsNullOrWhiteSpace(request.SignerSubject) ? existing?.SignerSubject ?? string.Empty : request.SignerSubject,
                Redistributable = request.ConfirmRedistribution,
                ModelPatterns = patterns,
                HardwareIdPatterns = hardwareIds,
                Sha256 = new DriverPackageHashes
                {
                    X64 = newHashes.TryGetValue("x64", out var x64) ? x64 : hashes.X64,
                    Arm64 = newHashes.TryGetValue("arm64", out var arm64) ? arm64 : hashes.Arm64,
                },
                Priority = request.Priority,
            };

            if (existing is not null)
            {
                manifest.Entries.Remove(existing);
            }

            manifest.Entries.Add(entry);
            manifest.LibraryVersion = $"local-{DateTime.Now:yyyyMMddHHmmss}";

            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, Options));
            _library.Reload();

            var message = existing is not null
                ? $"已更新本机驱动库中的「{request.Brand} {request.Family}」条目。"
                : $"已向本机驱动库新增「{request.Brand} {request.Family}」条目。";

            return new DriverLibraryWriteResult(true, message, manifestPath, infRelative, entry);
        }
        catch (Exception ex)
        {
            return DriverLibraryWriteResult.Fail($"写入驱动库失败:{ex.Message}");
        }
    }

    /// <summary>确保磁盘上存在清单文件;不存在时用当前清单播种。返回清单路径。</summary>
    public string? EnsureDiskManifest()
    {
        var path = Path.Combine(_library.Root, DriverLibrary.ManifestRelativePath);
        if (File.Exists(path))
        {
            return path;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_library.Manifest, Options));
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DriverManifest? ReadManifest(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<DriverManifest>(File.ReadAllText(path), Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string Slugify(string text)
    {
        var chars = text.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static string SanitizeSegment(string text)
        => new(text.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
}

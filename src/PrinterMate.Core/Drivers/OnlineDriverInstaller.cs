using PrinterMate.Catalog;
using PrinterMate.Security;

namespace PrinterMate.Core.Drivers;

/// <summary>下载并校验完成的结果。</summary>
public sealed record OnlineRetrievalResult(
    string FilePath,
    string Sha256,
    string SignerSubject,
    long Size,
    string Source,
    TimeSpan Elapsed);

/// <summary>解包并验证驱动包的结果。</summary>
public sealed record OnlinePackageResult(
    string ExtractDirectory,
    string InfPath,
    string InfFileName,
    string InfSha256,
    string CatalogPath,
    string SignerSubject,
    string Message);

/// <summary>
/// 在线补齐(L2)编排器。把「检索 → 下载 → 三重校验 → 解包 → 验证 → 入库」串成一条可解释的链路。
///
/// 安全约束由 <see cref="SecureDownloader"/> 与 <see cref="DriverPackageVerifier"/> 强制执行:
///   - 只允许白名单 HTTPS 域名,重定向逐跳复校;
///   - 下载后必须通过 SHA256 与 Authenticode(含 .cat 目录文件);
///   - 入库条目的 redistributable 由本机分发策略决定,不会凭下载行为自动放宽。
/// </summary>
public sealed class OnlineDriverInstaller
{
    private readonly OnlineDriverService _online;
    private readonly DriverLibrary _library;
    private readonly DriverLibraryWriter _writer;
    private readonly AuditLog _audit;

    public OnlineDriverInstaller(
        OnlineDriverService? online = null,
        DriverLibrary? library = null,
        AuditLog? audit = null)
    {
        _audit = audit ?? new AuditLog();
        _online = online ?? new OnlineDriverService(audit: _audit);
        _library = library ?? DriverLibrary.Load();
        _writer = new DriverLibraryWriter(_library);
    }

    public DriverLibrary Library => _library;

    /// <summary>下载目录:%LOCALAPPDATA%\PrinterMate\Downloads</summary>
    public static string DownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrinterMate",
        "Downloads");

    public Task<OperationResult<IReadOnlyList<OnlineDriverCandidate>>> SearchAsync(
        string brand,
        string? model,
        CancellationToken ct = default)
        => _online.SearchAsync(brand, model, ct);

    /// <summary>下载并完成白名单 / SHA256 / 签名三重校验。</summary>
    public async Task<OperationResult<OnlineRetrievalResult>> DownloadAsync(
        OnlineDriverCandidate candidate,
        IProgress<DownloadCheckpoint>? progress = null,
        CancellationToken ct = default)
    {
        var started = DateTimeOffset.Now;
        Directory.CreateDirectory(DownloadDirectory);

        var download = await _online
            .DownloadAsync(candidate, DownloadDirectory, expectedSignerSubject: null, progress, ct)
            .ConfigureAwait(false);

        if (!download.IsSuccess || download.Value is null)
        {
            return new OperationResult<OnlineRetrievalResult>
            {
                Status = download.Status,
                Message = download.Message,
                Suggestion = download.Suggestion,
            };
        }

        var filePath = download.Value;

        // 这里才做签名提取:SecureDownloader 内部已完成校验,此处只把证据取出来给用户看。
        var signature = Interop.AuthenticodeVerifier.Verify(filePath);
        var sha = Sha256Verifier.ComputeHex(filePath);
        var size = new FileInfo(filePath).Length;

        _audit.Write(
            "在线补齐-下载校验通过",
            candidate.DisplayName,
            "成功",
            $"文件={filePath};大小={size};签名主体={signature.SignerSubject}",
            candidate.Url,
            sha,
            signature.SignerSubject);

        // 注意:对 Microsoft Update Catalog 的候选,Url 字段存放的是 updateId(GUID)而不是 URL,
        // 直接 new Uri() 会抛 UriFormatException。这里做无异常解析,保留原始标识即可。
        var source = Uri.TryCreate(candidate.Url, UriKind.Absolute, out var parsed)
            ? parsed.ToString()
            : candidate.Url;

        return OperationResult<OnlineRetrievalResult>.Ok(
            new OnlineRetrievalResult(filePath, sha, signature.SignerSubject, size, source, DateTimeOffset.Now - started),
            $"已下载并通过校验:{Path.GetFileName(filePath)}({size / 1024} KB)");
    }

    /// <summary>解包并对解出来的驱动包做 SHA256 + .cat 签名验证。</summary>
    public OperationResult<OnlinePackageResult> ExtractAndVerify(string packagePath)
    {
        var extract = _online.Extract(packagePath);
        if (!extract.IsSuccess || extract.Value is null)
        {
            return new OperationResult<OnlinePackageResult>
            {
                Status = extract.Status,
                Message = extract.Message,
                Suggestion = extract.Suggestion,
            };
        }

        var infPath = extract.Value;
        var verification = DriverPackageVerifier.Verify(infPath);

        if (!verification.Passed)
        {
            _audit.Write("在线补齐-驱动包校验", infPath, "拒绝", verification.Message);
            return OperationResult<OnlinePackageResult>.Fail(
                verification.Message,
                verification.Suggestion,
                OperationStatus.NotPermitted);
        }

        _audit.Write(
            "在线补齐-驱动包校验通过",
            infPath,
            "成功",
            verification.Message,
            sourceUrl: string.Empty,
            sha256: verification.Sha256,
            signerSubject: verification.SignerSubject);

        return OperationResult<OnlinePackageResult>.Ok(
            new OnlinePackageResult(
                Path.GetDirectoryName(infPath) ?? string.Empty,
                infPath,
                Path.GetFileName(infPath),
                verification.Sha256,
                verification.CatalogPath,
                verification.SignerSubject,
                verification.Message),
            verification.Message);
    }

    /// <summary>把已解包并验证过的驱动包收进本机驱动库。</summary>
    public OperationResult<DriverCatalogEntry> ImportToLibrary(
        OnlinePackageResult package,
        string brand,
        string family,
        PrintDescriptionLanguage pdl,
        string driverName,
        string version = "",
        bool? confirmRedistribution = null)
    {
        // 默认跟随本机分发策略:个人自用场景开启过该品牌就直接置为可分发,
        // 否则保持 false(程序仍会拒绝本地安装,直到用户显式开启)。
        var confirm = confirmRedistribution ?? _library.Policy.IsEnabled(brand);

        var request = new DriverPackageInstallRequest(
            Brand: brand,
            Family: family,
            Pdl: pdl,
            DriverName: driverName,
            SourceDirectory: package.ExtractDirectory,
            InfFileName: package.InfFileName,
            Sha256: package.InfSha256,
            SignerSubject: package.SignerSubject,
            Arch: new[] { DriverMatchEngine.CurrentArchitecture },
            ConfirmRedistribution: confirm,
            Version: version);

        var result = _writer.AddOrUpdate(request);

        if (!result.Success)
        {
            _audit.Write("在线补齐-写入驱动库", driverName, "失败", result.Message);
            return OperationResult<DriverCatalogEntry>.Fail(
                result.Message,
                "请确认驱动库根目录可写,或改用 tools\\Import-DriverPackage.ps1 手工导入。");
        }

        _audit.Write(
            "在线补齐-写入驱动库",
            $"{brand} {family}",
            "成功",
            $"{result.Message};清单={result.ManifestPath};INF={result.InfRelativePath};可再分发={confirm}",
            sourceUrl: string.Empty,
            sha256: package.InfSha256,
            signerSubject: package.SignerSubject);

        var suffix = confirm
            ? string.Empty
            : "(该品牌的本机授权开关仍为关闭,安装前需在「内置驱动许可」中开启)";

        return result.Entry is null
            ? OperationResult<DriverCatalogEntry>.Fail(result.Message)
            : OperationResult<DriverCatalogEntry>.Ok(result.Entry, result.Message + suffix);
    }

    /// <summary>
    /// 从 INF 中解析出候选驱动名。
    ///
    /// 注意:厂商 INF 的驱动名并不总在 <c>[Models]</c> 段 —— HP 用的是
    /// <c>[HP]</c> / <c>[HP.NTamd64]</c> 这类以厂商名命名的段。
    /// 因此这里扫描除 <c>[Version]</c>/<c>[Strings]</c>/<c>[Manufacturer]</c> 之外的所有段,
    /// 收集形如 <c>"驱动名" = 引用</c> 的条目。
    /// </summary>
    public static IReadOnlyList<string> ReadDriverNamesFromInf(string infPath)
    {
        var names = new List<string>();

        try
        {
            var skipSection = false;

            foreach (var raw in File.ReadLines(infPath))
            {
                var line = raw.Trim();

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    var section = line[1..^1].Trim();
                    skipSection = section.Equals("Version", StringComparison.OrdinalIgnoreCase)
                        || section.Equals("Strings", StringComparison.OrdinalIgnoreCase)
                        || section.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (skipSection || line.Length == 0 || line.StartsWith(';'))
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                // 设备条目形如:「"驱动名" = 安装节, 硬件ID」。
                // 借此把 CopyFiles=@x.dll / Signature="$Windows NT$" 这类指令排除掉。
                if (!line[(separator + 1)..].Contains(','))
                {
                    continue;
                }

                var left = line[..separator].Trim().Trim('"');
                if (left.Length == 0 || left.StartsWith('%'))
                {
                    continue;
                }

                names.Add(left);
            }
        }
        catch (Exception)
        {
            // INF 读取失败时返回空列表,由调用方提示用户手工填写。
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>从驱动名推断页面描述语言(规格:同品牌 PCL6/PS 并存时优先 PCL6)。</summary>
    public static PrintDescriptionLanguage InferPdl(string driverName)
    {
        if (driverName.Contains("PCL 6", StringComparison.OrdinalIgnoreCase)
            || driverName.Contains("PCL6", StringComparison.OrdinalIgnoreCase))
        {
            return PrintDescriptionLanguage.PCL6;
        }

        if (driverName.Contains("PostScript", StringComparison.OrdinalIgnoreCase)
            || driverName.Contains("PS", StringComparison.Ordinal))
        {
            return PrintDescriptionLanguage.PS;
        }

        if (driverName.Contains("GDI", StringComparison.OrdinalIgnoreCase))
        {
            return PrintDescriptionLanguage.GDI;
        }

        return PrintDescriptionLanguage.PCL6;
    }
}

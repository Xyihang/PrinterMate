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
    string Message,
    IReadOnlyList<InfChoice> InfCandidates,
    string ChoiceReason);

/// <summary>批量操作(批量下载 / 批量导入)的单项结果。</summary>
public sealed record BatchItemResult(string Name, bool Success, string Message);

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

    /// <summary>
    /// 为一个品牌拼出可用的检索词。
    ///
    /// 为什么需要:Microsoft Update Catalog 是短语匹配,用单字 "HP" / "Canon" 检索会返回 0 条。
    /// 这里取该品牌所有型号模式里最长的固定前缀,拼成「品牌 前缀」,例如
    /// HP -> "HP LaserJet"、Canon -> "Canon imageCLASS"、Zebra -> "Zebra ZDesigner"。
    /// 取不到合适前缀时退回品牌名本身。
    /// </summary>
    public string BuildBrandQuery(string brand)
    {
        var literal = _library.ForBrand(brand)
            .Where(e => !e.IsInboxDriver)
            .SelectMany(e => e.ModelPatterns)
            .Select(p => p.Replace("*", string.Empty).Replace("?", string.Empty).Trim())
            .Where(p => p.Length >= 3 && !p.Contains(' '))
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(literal) ? brand : $"{brand} {literal}";
    }

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

    /// <summary>
    /// 解包并对解出来的驱动包做 SHA256 + .cat 签名验证。
    ///
    /// <paramref name="variantHint"/> 传入所下候选的标题(形如「… HP Deskjet 2520 series (USB)」),
    /// 用于在同一个 .cab 内的多个 INF 变体(VPL 打印驱动 / REST 桩 / 扫描仪 / 空桩)中挑对那一个。
    /// 会按分数从高到低依次尝试,取第一个签名校验通过的 —— 这样即使排序猜错也不会装错。
    /// </summary>
    public OperationResult<OnlinePackageResult> ExtractAndVerify(string packagePath, string? variantHint = null)
    {
        var extracted = _online.ExtractPackage(packagePath);
        if (!extracted.IsSuccess || extracted.Value is null)
        {
            return new OperationResult<OnlinePackageResult>
            {
                Status = extracted.Status,
                Message = extracted.Message,
                Suggestion = extracted.Suggestion,
            };
        }

        var directory = extracted.Value;
        var infPaths = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.inf", SearchOption.AllDirectories).ToList()
            : new List<string>();

        if (infPaths.Count == 0)
        {
            _audit.Write("在线补齐-解包", packagePath, "失败", "解包目录内没有任何 INF");
            return OperationResult<OnlinePackageResult>.Fail(
                "解包后没有找到任何 INF 文件。",
                "该压缩包可能不是打印机驱动包,请确认来源。");
        }

        var ranked = OnlineDriverService.RankInfs(infPaths, variantHint);
        _audit.Write(
            "在线补齐-INF 排序",
            Path.GetFileName(packagePath),
            "完成",
            string.Join(" | ", ranked.Select(r => $"{r.FileName}={r.Score}({r.Reason})")));

        OnlinePackageResult? lastFailure = null;

        foreach (var choice in ranked)
        {
            var verification = DriverPackageVerifier.Verify(choice.InfPath);

            if (!verification.Passed)
            {
                lastFailure = new OnlinePackageResult(
                    Path.GetDirectoryName(choice.InfPath) ?? directory,
                    choice.InfPath,
                    choice.FileName,
                    verification.Sha256,
                    verification.CatalogPath,
                    verification.SignerSubject,
                    verification.Message,
                    ranked,
                    choice.Reason);

                _audit.Write("在线补齐-驱动包校验", choice.InfPath, "跳过", verification.Message);
                continue;
            }

            _audit.Write(
                "在线补齐-驱动包校验通过",
                choice.InfPath,
                "成功",
                $"{verification.Message};选中原因={choice.Reason}",
                sourceUrl: string.Empty,
                sha256: verification.Sha256,
                signerSubject: verification.SignerSubject);

            return OperationResult<OnlinePackageResult>.Ok(
                new OnlinePackageResult(
                    Path.GetDirectoryName(choice.InfPath) ?? directory,
                    choice.InfPath,
                    choice.FileName,
                    verification.Sha256,
                    verification.CatalogPath,
                    verification.SignerSubject,
                    verification.Message,
                    ranked,
                    choice.Reason),
                $"{verification.Message}(共发现 {ranked.Count} 个 INF,已选 {choice.FileName}:{choice.Reason})");
        }

        var failure = lastFailure!;
        return OperationResult<OnlinePackageResult>.Fail(
            failure.Message,
            "包内所有 INF 都没能通过签名校验,请确认来源;或改用厂商官网的完整驱动包。",
            OperationStatus.NotPermitted);
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
    /// 批量下载:把各品牌的官方驱动包下到下载目录,**不导入、不改动驱动库**。
    ///
    /// 设计边界:调用方必须已经取得使用者对该厂商许可条款的显式同意
    /// (由 <see cref="RecordLicenseAcceptance"/> 留痕),本方法不会替用户接受任何条款。
    /// </summary>
    public async Task<IReadOnlyList<BatchItemResult>> DownloadAllAsync(
        IReadOnlyList<string> brands,
        int candidatesPerBrand = 3,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<BatchItemResult>();
        Directory.CreateDirectory(DownloadDirectory);

        foreach (var brand in brands)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"[{brand}] 检索 Microsoft Update Catalog …");

            try
            {
                // 只用品牌名检索是不行的:Microsoft Update Catalog 是短语匹配,
                // 单字 "HP" / "Canon" 会返回 0 条。必须带上一个具体型号关键词,
                // 这里从该品牌清单条目的 modelPatterns 里取最长的字面量来拼。
                var query = BuildBrandQuery(brand);
                progress?.Report($"[{brand}] 检索 Microsoft Update Catalog(关键词:{query})…");

                var search = await SearchAsync(query, null, ct).ConfigureAwait(false);
                if (!search.IsSuccess || search.Value is null || search.Value.Count == 0)
                {
                    results.Add(new BatchItemResult(brand, false, search.Message));
                    continue;
                }

                var tried = 0;
                var succeeded = false;

                foreach (var candidate in search.Value)
                {
                    if (tried >= candidatesPerBrand)
                    {
                        break;
                    }

                    tried++;
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"[{brand}] 下载 {candidate.DisplayName}({candidate.Size})…");

                    var download = await DownloadAsync(candidate, null, ct).ConfigureAwait(false);
                    if (!download.IsSuccess || download.Value is null)
                    {
                        progress?.Report($"[{brand}] 该候选未通过校验:{download.Message}");
                        continue;
                    }

                    var sizeMb = new FileInfo(download.Value.FilePath).Length / 1024 / 1024;
                    results.Add(new BatchItemResult(
                        brand,
                        true,
                        $"{Path.GetFileName(download.Value.FilePath)}({sizeMb} MB) — {candidate.DisplayName}"));

                    progress?.Report($"[{brand}] ✅ 已下载:{download.Value.FilePath}");
                    succeeded = true;
                    break;
                }

                if (!succeeded)
                {
                    results.Add(new BatchItemResult(brand, false, $"尝试了 {tried} 个候选,均未通过校验"));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new BatchItemResult(brand, false, ex.Message));
            }
        }

        _audit.Write(
            "批量下载-完成",
            DownloadDirectory,
            "完成",
            $"共 {brands.Count} 个品牌,成功 {results.Count(r => r.Success)} 个;均为只下载不入库");

        return results;
    }

    /// <summary>
    /// 批量导入:把一个文件夹里的驱动包逐个「解包 → 验证签名 → 入库」。
    /// 品牌从 INF 里解析出的驱动名推断;推断不出来的会明确报出来,让你用「选择本地驱动包…」手动指定。
    /// </summary>
    public IReadOnlyList<BatchItemResult> ImportFolder(string folder, IProgress<string>? progress = null)
    {
        if (!Directory.Exists(folder))
        {
            return new[] { new BatchItemResult(folder, false, "文件夹不存在。") };
        }

        var extensions = new[] { ".cab", ".zip", ".exe", ".msi", ".inf" };
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            return new[] { new BatchItemResult(folder, false, "文件夹里没有可识别的驱动包(.cab / .zip / .exe / .msi / .inf)。") };
        }

        var results = new List<BatchItemResult>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            progress?.Report($"处理 {name} …");

            try
            {
                var package = ExtractAndVerify(file, name);
                if (!package.IsSuccess || package.Value is null)
                {
                    results.Add(new BatchItemResult(name, false, package.Message));
                    continue;
                }

                // 驱动名必须与 INF 逐字一致:取打分最高的那个 INF 解析出的名字
                var driverName = package.Value.InfCandidates
                    .SelectMany(c => c.DriverNames)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(driverName))
                {
                    results.Add(new BatchItemResult(name, false, "包内 INF 解析不出驱动名,请用「选择本地驱动包…」手动指定。"));
                    continue;
                }

                var brand = CatalogMatcher.ResolveBrands(null, driverName, _library).FirstOrDefault()
                            ?? CatalogMatcher.ResolveBrands(null, name, _library).FirstOrDefault();

                if (string.IsNullOrWhiteSpace(brand))
                {
                    results.Add(new BatchItemResult(
                        name,
                        false,
                        $"无法从「{driverName}」推断品牌,请用「选择本地驱动包…」手动指定。"));
                    continue;
                }

                var import = ImportToLibrary(
                    package.Value,
                    brand,
                    $"{brand} 在线补齐",
                    InferPdl(driverName),
                    driverName);

                results.Add(import.IsSuccess
                    ? new BatchItemResult(name, true, $"{brand} / {driverName}")
                    : new BatchItemResult(name, false, import.Message));
            }
            catch (Exception ex)
            {
                results.Add(new BatchItemResult(name, false, ex.Message));
            }
        }

        _audit.Write(
            "批量导入-完成",
            folder,
            "完成",
            $"共 {files.Count} 个文件,成功 {results.Count(r => r.Success)} 个");

        return results;
    }

    /// <summary>
    /// 记录使用者对某厂商许可条款的显式确认。
    ///
    /// 为什么要留痕:厂商驱动的许可条款是给最终用户的,程序不能静默替用户接受。
    /// 把"谁在什么时候接受了哪个厂商的条款"记进审计日志,既是对用户的交代,也是合规上的凭据。
    /// </summary>
    public void RecordLicenseAcceptance(string brand, string sourceUrl, string licensePageUrl)
    {
        _audit.Write(
            "许可确认",
            brand,
            "已接受",
            $"使用者确认已阅读并接受「{brand}」驱动许可条款;许可页={licensePageUrl};下载源={sourceUrl}",
            sourceUrl);
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

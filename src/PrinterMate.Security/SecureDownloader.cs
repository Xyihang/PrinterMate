using System.Diagnostics;
using System.Net;

namespace PrinterMate.Security;

/// <summary>安全下载的阶段。</summary>
public enum DownloadStage
{
    CheckingSource,
    Downloading,
    Hashing,
    VerifyingSignature,
    Done,
}

/// <summary>下载进度检查点。</summary>
public sealed record DownloadCheckpoint(DownloadStage Stage, long BytesReceived, string Message);

/// <summary>下载完成结果。</summary>
public sealed record DownloadResult(
    string FilePath,
    string Sha256,
    string SignerSubject,
    long Size,
    Uri Source,
    TimeSpan Elapsed);

/// <summary>
/// 安全下载器:白名单 → 下载 → SHA256 → Authenticode → 发布。
/// 仅允许 https;每一次重定向都重新校验白名单;超过 800 MB 立即中止;
/// 全程先写 <c>.part</c> 临时文件,任何一步失败都会删除它。
/// </summary>
public sealed class SecureDownloader : IDisposable
{
    private const long MaxBytes = 800L * 1024 * 1024;
    private const int MaxRedirects = 5;
    private const int BufferSize = 81920;

    private readonly DownloadSourceAllowlist _allowlist;
    private readonly TrustLedger? _ledger;
    private readonly AuditLog? _audit;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public SecureDownloader(
        DownloadSourceAllowlist? allowlist = null,
        TrustLedger? ledger = null,
        AuditLog? audit = null)
        : this(allowlist, ledger, audit, CreateClient())
    {
        _ownsHttpClient = true;
    }

    /// <summary>测试用:注入自定义 HttpClient(不会自动重定向,由下载器自行校验每次跳转)。</summary>
    internal SecureDownloader(
        DownloadSourceAllowlist? allowlist,
        TrustLedger? ledger,
        AuditLog? audit,
        HttpClient http)
    {
        _allowlist = allowlist ?? DownloadSourceAllowlist.Default;
        _ledger = ledger;
        _audit = audit;
        _http = http;
    }

    public async Task<DownloadResult> DownloadAsync(
        string url,
        string destinationPath,
        string? expectedSha256 = null,
        string? expectedSignerSubject = null,
        string? expectedBrand = null,
        IProgress<DownloadCheckpoint>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new SecurityPolicyException("目标路径为空,无法保存下载文件。", "请提供有效的保存路径。");
        }

        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new DownloadCheckpoint(DownloadStage.CheckingSource, 0, "正在校验下载来源..."));

        // 步骤 1:白名单校验(此时尚未发起任何网络请求)。
        var check = _allowlist.Check(url ?? string.Empty, expectedBrand);
        if (!check.IsAllowed)
        {
            _audit?.Write("download.policy", url ?? string.Empty, "拒绝", check.Reason, url ?? string.Empty);
            throw new SecurityPolicyException(check.Reason, SuggestionFor(check.Verdict, expectedBrand));
        }

        _audit?.Write("download.source", url!, "通过", check.Reason, url!);
        var source = new Uri(url!.Trim(), UriKind.Absolute);

        var finalPath = Path.GetFullPath(destinationPath);
        var partPath = finalPath + ".part";
        var parent = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(parent))
        {
            System.IO.Directory.CreateDirectory(parent);
        }

        var committed = false;
        try
        {
            string computedSha;
            string signerSubject;
            long size;

            using (var response = await SendFollowingRedirectsAsync(source, expectedBrand, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    var reason = $"下载失败:服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}。";
                    _audit?.Write("download.http", url!, "失败", reason, url!);
                    throw new SecurityPolicyException(reason, "请稍后重试,或改用厂商官方镜像地址。");
                }

                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength is > MaxBytes)
                {
                    var reason = $"文件大小 {contentLength.Value} 字节超过 800 MB 上限,已中止下载。";
                    _audit?.Write("download.size", url!, "失败", reason, url!);
                    throw new SecurityPolicyException(reason, "请下载所需的单独组件,而不要下载超大的整包。");
                }

                size = await DownloadToPartAsync(response, partPath, url!, progress, ct).ConfigureAwait(false);

                progress?.Report(new DownloadCheckpoint(DownloadStage.Hashing, size, "正在计算 SHA256..."));
                computedSha = await Sha256Verifier.ComputeHexAsync(partPath, ct).ConfigureAwait(false);
                _audit?.Write("download.hash", url!, "通过", $"文件 SHA256={computedSha}", url!, computedSha);

                // 步骤 3:SHA256 期望值或信任账本校验。
                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    if (!Sha256Verifier.Equals(expectedSha256, computedSha))
                    {
                        var reason = $"SHA256 校验失败:期望 {expectedSha256},实际 {computedSha},文件可能被篡改或下载不完整,已拒绝。";
                        _audit?.Write("download.hash", url!, "拒绝", reason, url!, computedSha);
                        throw new SecurityPolicyException(reason, "请从厂商官方页面重新获取文件与校验值。");
                    }
                }
                else
                {
                    if (_ledger is null)
                    {
                        const string reason = "没有提供期望 SHA256,且未配置信任账本,无法确认文件完整性,已拒绝。";
                        _audit?.Write("download.ledger", url!, "拒绝", reason, url!, computedSha);
                        throw new SecurityPolicyException(reason, "请在下载时提供厂商公布的 SHA256 校验值。");
                    }

                    var ledgerKey = BuildLedgerKey(url!, finalPath);
                    if (!_ledger.VerifyOrPin(ledgerKey, computedSha, expectedSignerSubject ?? string.Empty, url!, out var ledgerReason))
                    {
                        _audit?.Write("download.ledger", url!, "拒绝", ledgerReason, url!, computedSha);
                        throw new SecurityPolicyException(ledgerReason, "请人工确认来源后清理信任账本中的旧记录,再重新下载。");
                    }

                    _audit?.Write("download.ledger", url!, "通过", ledgerReason, url!, computedSha);
                }

                // 步骤 4:Authenticode 签名校验(无论是否有期望哈希都必须通过)。
                progress?.Report(new DownloadCheckpoint(DownloadStage.VerifyingSignature, size, "正在校验数字签名..."));
                var signature = new SignaturePolicy().Verify(partPath, expectedSignerSubject, expectedBrand);
                if (!signature.Passed)
                {
                    _audit?.Write("download.signature", url!, "拒绝", signature.Reason, url!, computedSha, signature.SignerSubject);
                    throw new SecurityPolicyException(signature.Reason, signature.Suggestion);
                }

                _audit?.Write("download.signature", url!, "通过", signature.Reason, url!, computedSha, signature.SignerSubject);
                signerSubject = signature.SignerSubject;
            }

            // 步骤 5:校验全部通过后才发布文件。
            File.Move(partPath, finalPath, overwrite: true);
            committed = true;
            stopwatch.Stop();

            progress?.Report(new DownloadCheckpoint(DownloadStage.Done, size, "下载并校验完成。"));
            _audit?.Write("download.done", url!, "成功",
                $"已保存到 {finalPath},大小 {size} 字节,耗时 {stopwatch.Elapsed.TotalSeconds:F1} 秒。",
                url!, computedSha, signerSubject);

            return new DownloadResult(finalPath, computedSha, signerSubject, size, source, stopwatch.Elapsed);
        }
        finally
        {
            if (!committed && File.Exists(partPath))
            {
                try
                {
                    File.Delete(partPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        Uri source, string? expectedBrand, CancellationToken ct)
    {
        var current = source;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                response.Dispose();
                var reason = $"服务器返回重定向 {(int)response.StatusCode},但没有提供 Location,已中止下载。";
                _audit?.Write("download.redirect", current.ToString(), "拒绝", reason, current.ToString());
                throw new SecurityPolicyException(reason, "请联系下载来源确认下载地址是否有效。");
            }

            if (redirects >= MaxRedirects)
            {
                response.Dispose();
                var reason = $"重定向次数超过 {MaxRedirects} 次上限,已中止下载。";
                _audit?.Write("download.redirect", current.ToString(), "拒绝", reason, current.ToString());
                throw new SecurityPolicyException(reason, "请直接使用最终的文件下载地址。");
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);

            // 每一次重定向之后都必须重新走白名单校验。
            var nextCheck = _allowlist.Check(next, expectedBrand);
            if (!nextCheck.IsAllowed)
            {
                response.Dispose();
                var reason = $"重定向目标未通过来源校验:{nextCheck.Reason}";
                _audit?.Write("download.redirect", next.ToString(), "拒绝", reason, current.ToString());
                throw new SecurityPolicyException(reason, SuggestionFor(nextCheck.Verdict, expectedBrand));
            }

            _audit?.Write("download.redirect", next.ToString(), "通过",
                $"重定向到 {next.Host} 已通过白名单校验。", current.ToString());
            response.Dispose();
            current = next;
        }
    }

    private async Task<long> DownloadToPartAsync(
        HttpResponseMessage response,
        string partPath,
        string url,
        IProgress<DownloadCheckpoint>? progress,
        CancellationToken ct)
    {
        long total = 0;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = new FileStream(
            partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
        {
            // 空闲看门狗。
            //
            // 为什么必须要有:上面用了 HttpCompletionOption.ResponseHeadersRead,
            // 此时 HttpClient.Timeout 只覆盖"收到响应头",**不覆盖读响应体**。
            // 服务器一旦在传输中途停止吐数据,下面的 ReadAsync 会永久阻塞 ——
            // 既没有超时也不会被取消(实测表现为一个 0 字节的 .part 文件一直挂在那里)。
            // 这里用"距上次收到数据的间隔"来判定卡死,而不是用总时长,
            // 避免把正常的大文件慢速下载误杀。
            var idleTimeout = TimeSpan.FromSeconds(60);
            var lastDataAt = DateTime.UtcNow;

            using var watchdog = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, watchdog.Token);

            using var timer = new Timer(
                _ =>
                {
                    if (DateTime.UtcNow - lastDataAt > idleTimeout)
                    {
                        try
                        {
                            watchdog.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            // 已经收尾,忽略
                        }
                    }
                },
                state: null,
                dueTime: TimeSpan.FromSeconds(10),
                period: TimeSpan.FromSeconds(10));

            try
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                {
                    lastDataAt = DateTime.UtcNow;
                    total += read;
                    if (total > MaxBytes)
                    {
                        var reason = $"下载内容已超过 800 MB 上限,已中止并删除临时文件。";
                        _audit?.Write("download.size", url, "拒绝", reason, url);
                        throw new SecurityPolicyException(reason, "请确认下载的是所需的单独组件。");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                    progress?.Report(new DownloadCheckpoint(DownloadStage.Downloading, total, $"已接收 {total} 字节"));
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                var reason = $"下载已卡死:{idleTimeout.TotalSeconds:F0} 秒内没有收到任何数据,已中止并删除临时文件。";
                _audit?.Write("download.idle-timeout", url, "拒绝", reason, url);
                throw new SecurityPolicyException(
                    reason,
                    "请检查网络或代理是否稳定,或稍后重试;也可以改用厂商官网手动下载后用「选择本地驱动包…」导入。");
            }

            await destination.FlushAsync(ct).ConfigureAwait(false);
        }

        return total;
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static string BuildLedgerKey(string url, string finalPath)
        => $"url:{url}|path:{Path.GetFileName(finalPath)}";

    private static string SuggestionFor(SourceVerdict verdict, string? expectedBrand) => verdict switch
    {
        SourceVerdict.NotHttps => "请改用 https 官方地址下载。",
        SourceVerdict.NotAllowlisted => "请从微软更新服务或打印机厂商官方网站获取驱动。",
        SourceVerdict.VendorMismatch =>
            $"请确认下载来源与设备品牌「{expectedBrand ?? "未知"}」一致,或改为厂商官网下载。",
        SourceVerdict.Malformed => "请检查下载地址是否完整正确。",
        _ => "请更换为官方可信下载地址。",
    };

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            // 禁用自动重定向:每一次跳转都必须由本类重新做白名单校验。
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}

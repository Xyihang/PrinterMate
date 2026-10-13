using System.IO;
using System.Net.Http;
using SharpIpp;
using SharpIpp.Exceptions;
using SharpIpp.Models.Requests;
using SharpIpp.Models.Responses;
using SharpIpp.Protocol.Models;

namespace PrinterMate.Printing;

/// <summary>IPP 设备能力信息(免驱通道)。</summary>
public sealed record IppDeviceInfo(
    string Uri, string MakeAndModel, string Manufacturer, string PrinterState,
    IReadOnlyList<string> DocumentFormats, IReadOnlyList<string> UrfSupported,
    string Uuid, bool SupportsIppEverywhere, string Info);

/// <summary>基于 SharpIppNext 的最小 IPP 客户端,只暴露免驱打印需要的三个操作。</summary>
public sealed class IppClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _timeout;

    public IppClient(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), _timeout, "超时时间必须大于 0。");
        }
    }

    /// <summary>Get-Printer-Attributes。失败抛中文异常。</summary>
    public async Task<IppDeviceInfo> GetAttributesAsync(Uri ippUri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ippUri);

        var request = new GetPrinterAttributesRequest
        {
            Version = new IppVersion(1, 1),
            OperationAttributes = new GetPrinterAttributesOperationAttributes
            {
                PrinterUri = ippUri,
                RequestingUserName = Environment.UserName,
                AttributesCharset = Charset.Utf8,
                AttributesNaturalLanguage = "zh-cn",
            },
        };

        var response = await SendAsync(client => client.GetPrinterAttributesAsync(request, ct), ippUri, "读取打印机属性", ct)
            .ConfigureAwait(false);

        var attributes = response.PrinterAttributes;
        if (attributes is null)
        {
            throw new InvalidOperationException(
                $"打印机「{ippUri}」未返回任何属性。请确认该地址指向的是支持 IPP 的打印服务(通常为 /ipp/print)。");
        }

        var formats = attributes.DocumentFormatSupported?.Value ?? Array.Empty<string>();
        var urf = formats
            .Where(f => f.Contains("urf", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new IppDeviceInfo(
            ippUri.ToString(),
            attributes.PrinterMakeAndModel?.Value ?? string.Empty,
            DeriveManufacturer(attributes.PrinterMakeAndModel?.Value),
            MapPrinterState(attributes.PrinterState?.Value),
            formats,
            urf,
            attributes.PrinterUUID?.Value ?? string.Empty,
            urf.Length > 0,
            attributes.PrinterInfo?.Value ?? string.Empty);
    }

    /// <summary>Validate-Job:只校验不落纸,用于「能不能打」的判定。</summary>
    public async Task<bool> ValidateJobAsync(Uri ippUri, string documentFormat = "application/pdf", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ippUri);
        if (string.IsNullOrWhiteSpace(documentFormat))
        {
            throw new ArgumentException("文档格式为空。请使用 MIME 类型,例如 application/pdf。", nameof(documentFormat));
        }

        var request = new ValidateJobRequest
        {
            Version = new IppVersion(1, 1),
            OperationAttributes = new ValidateJobOperationAttributes
            {
                PrinterUri = ippUri,
                RequestingUserName = Environment.UserName,
                DocumentFormat = new DocumentFormat(documentFormat),
                AttributesCharset = Charset.Utf8,
                AttributesNaturalLanguage = "zh-cn",
            },
            JobTemplateAttributes = new JobTemplateAttributes(),
        };

        try
        {
            var response = await SendAsync(client => client.ValidateJobAsync(request, ct), ippUri, "校验打印任务", ct)
                .ConfigureAwait(false);
            return IsSuccess(response.StatusCode);
        }
        catch (IppResponseException)
        {
            // 服务器明确拒绝了该任务,属于「不能打」的正常判定结果。
            return false;
        }
        catch (IppRequestException)
        {
            return false;
        }
    }

    /// <summary>Print-Job,返回 job-id。</summary>
    public async Task<int> PrintBytesAsync(Uri ippUri, byte[] data, string documentName, string documentFormat, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ippUri);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            throw new ArgumentException("打印数据为空,没有可发送的内容。", nameof(data));
        }

        if (string.IsNullOrWhiteSpace(documentName))
        {
            throw new ArgumentException("文档名称为空。请提供用于队列显示的名称。", nameof(documentName));
        }

        if (string.IsNullOrWhiteSpace(documentFormat))
        {
            throw new ArgumentException("文档格式为空。请使用 MIME 类型,例如 application/pdf。", nameof(documentFormat));
        }

        using var stream = new MemoryStream(data, writable: false);
        var request = new PrintJobRequest
        {
            Version = new IppVersion(1, 1),
            Document = stream,
            JobTemplateAttributes = new JobTemplateAttributes(),
            OperationAttributes = new PrintJobOperationAttributes
            {
                PrinterUri = ippUri,
                RequestingUserName = Environment.UserName,
                DocumentName = documentName,
                DocumentFormat = new DocumentFormat(documentFormat),
                AttributesCharset = Charset.Utf8,
                AttributesNaturalLanguage = "zh-cn",
            },
        };

        var response = await SendAsync(client => client.PrintJobAsync(request, ct), ippUri, "提交 IPP 打印任务", ct)
            .ConfigureAwait(false);

        var attributes = response.JobAttributes;
        if (attributes is null)
        {
            throw new InvalidOperationException(
                $"打印机「{ippUri}」接受了任务但未返回作业编号。请到打印机自带的 Web 管理页面确认任务是否已排队。");
        }

        return attributes.JobId;
    }

    /// <summary>便捷方法:构造 ipp://host:631/ipp/print。</summary>
    public static Uri BuildIppUri(string host, int port = 631, string resource = "/ipp/print")
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("打印机地址为空。请填写 IP 地址或主机名,例如 192.168.1.50。", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "端口号必须在 1-65535 之间。");
        }

        var raw = host.Trim();
        var scheme = "ipp";

        var schemeSeparator = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            var schemeText = raw[..schemeSeparator].ToLowerInvariant();
            scheme = schemeText is "ipps" or "https" ? "ipps" : "ipp";
            raw = raw[(schemeSeparator + 3)..];
        }

        var slashIndex = raw.IndexOf('/');
        if (slashIndex >= 0)
        {
            raw = raw[..slashIndex];
        }

        if (raw.Length == 0)
        {
            throw new ArgumentException($"打印机地址「{host}」无效:缺少主机名。请填写 IP 地址或主机名,例如 192.168.1.50。", nameof(host));
        }

        var hostName = raw;
        var effectivePort = port;

        if (Uri.TryCreate($"{scheme}://{raw}", UriKind.Absolute, out var parsed) && parsed.Host.Length > 0)
        {
            hostName = parsed.Host;
            if (parsed.Port > 0 && !parsed.IsDefaultPort)
            {
                effectivePort = parsed.Port;
            }
        }

        var path = string.IsNullOrWhiteSpace(resource) ? "/ipp/print" : resource.Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        var authority = hostName.Contains(':') && !hostName.StartsWith('[')
            ? $"[{hostName}]"
            : hostName;

        return new Uri($"{scheme}://{authority}:{effectivePort}{path}", UriKind.Absolute);
    }

    private HttpClient CreateHttpClient() => new()
    {
        Timeout = _timeout,
    };

    private async Task<T> SendAsync<T>(Func<SharpIppClient, Task<T>> operation, Uri ippUri, string action, CancellationToken ct)
    {
        using var http = CreateHttpClient();
        using var client = new SharpIppClient(http);
        try
        {
            return await operation(client).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not ArgumentException)
        {
            throw Wrap(action, ippUri, ex);
        }
    }

    private InvalidOperationException Wrap(string action, Uri ippUri, Exception ex)
    {
        var hint = ex switch
        {
            HttpRequestException => "请确认打印机已开机、与本机处于同一网段,且 631 端口未被防火墙或安全软件拦截。",
            OperationCanceledException or TaskCanceledException =>
                $"请求在 {_timeout.TotalSeconds:0} 秒内未完成。请检查网络连通性,或在调用时放宽超时时间。",
            _ => "请确认目标地址是支持 IPP 的打印服务(通常为 ipp://主机:631/ipp/print)。",
        };

        return new InvalidOperationException($"{action}失败[{ippUri}]:{ex.Message}。{hint}", ex);
    }

    private static bool IsSuccess(IppStatusCode statusCode) => (int)statusCode < 0x0100;

    private static string MapPrinterState(PrinterState? state) => state switch
    {
        SharpIpp.Protocol.Models.PrinterState.Idle => "idle",
        SharpIpp.Protocol.Models.PrinterState.Processing => "processing",
        SharpIpp.Protocol.Models.PrinterState.Stopped => "stopped",
        _ => "unknown",
    };

    private static string DeriveManufacturer(string? makeAndModel)
    {
        if (string.IsNullOrWhiteSpace(makeAndModel))
        {
            return string.Empty;
        }

        var space = makeAndModel.IndexOf(' ');
        return space > 0 ? makeAndModel[..space] : makeAndModel;
    }
}

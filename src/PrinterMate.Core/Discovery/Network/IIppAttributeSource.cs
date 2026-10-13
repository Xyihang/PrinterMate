using System.ComponentModel;
using PrinterMate.Printing;

namespace PrinterMate.Core.Discovery;

/// <summary>IPP Get-Printer-Attributes 的结果(与 PrinterMate.Printing.IppDeviceInfo 字段一一对应)。</summary>
public sealed record IppDeviceAttributes(
    string Uri,
    string MakeAndModel,
    string Manufacturer,
    string PrinterState,
    IReadOnlyList<string> DocumentFormats,
    IReadOnlyList<string> UrfSupported,
    string Uuid,
    bool SupportsIppEverywhere,
    string Info);

/// <summary>IPP 属性来源抽象:便于注入替身做单元测试,也避免发现层与打印层强耦合。</summary>
public interface IIppAttributeSource
{
    /// <summary>读取 IPP 属性;不可用时返回 null(不得抛异常)。</summary>
    Task<IppDeviceAttributes?> GetAttributesAsync(Uri ippUri, CancellationToken ct);
}

/// <summary>默认实现:直接复用 PrinterMate.Printing.IppClient(IPP 编码由打印层负责,发现层不重复实现)。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PrintingIppAttributeSource : IIppAttributeSource
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly IppClient _client;

    public PrintingIppAttributeSource(TimeSpan? timeout = null)
    {
        var effective = timeout is { Ticks: > 0 } ? timeout.Value : DefaultTimeout;
        _client = new IppClient(effective);
    }

    /// <summary>打印层已交付,属性查询可用。</summary>
    public static bool IsAvailable => true;

    /// <inheritdoc />
    public async Task<IppDeviceAttributes?> GetAttributesAsync(Uri ippUri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ippUri);

        try
        {
            var info = await _client.GetAttributesAsync(ippUri, ct).ConfigureAwait(false);
            return Map(info);
        }
        catch (Exception)
        {
            // 设备不支持 IPP / 网络异常 / 取消:一律降级为“无属性”,由端口探测结果兜底。
            return null;
        }
    }

    private static IppDeviceAttributes Map(IppDeviceInfo info) => new(
        info.Uri,
        info.MakeAndModel,
        info.Manufacturer,
        info.PrinterState,
        info.DocumentFormats,
        info.UrfSupported,
        info.Uuid,
        info.SupportsIppEverywhere,
        info.Info);
}

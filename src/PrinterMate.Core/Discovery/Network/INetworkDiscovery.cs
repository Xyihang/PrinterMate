namespace PrinterMate.Core.Discovery;

/// <summary>单个局域网发现协议(SNMP / mDNS / WS-Discovery …)。实现必须永不抛异常。</summary>
public interface INetworkDiscovery : IDisposable
{
    /// <summary>发现器中文名,用于进度提示与结果来源标注。</summary>
    string Name { get; }

    /// <summary>执行一次发现;超时或环境不可用时返回已收集到的结果(可能为空)。</summary>
    Task<IReadOnlyList<DiscoveredPrinter>> DiscoverAsync(DiscoveryOptions options, CancellationToken ct);
}

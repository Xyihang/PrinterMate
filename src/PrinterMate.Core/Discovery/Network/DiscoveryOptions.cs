using System.Net;

namespace PrinterMate.Core.Discovery;

/// <summary>局域网设备发现选项。</summary>
/// <param name="Timeout">整次发现的总超时;超时后返回已收集到的结果。</param>
/// <param name="Subnets">额外探测目标(广播地址或网段地址);为空时使用本机网卡推导出的定向广播地址。</param>
public sealed record DiscoveryOptions(
    TimeSpan Timeout,
    IReadOnlyList<IPAddress>? Subnets = null,
    bool EnableSnmp = true,
    bool EnableMdns = true,
    bool EnableWsd = true,
    bool EnableIppProbe = true,
    bool EnableRawProbe = true)
{
    /// <summary>默认选项:总超时 8 秒,全部发现器开启。</summary>
    public static DiscoveryOptions Default { get; } = new(TimeSpan.FromSeconds(8));

    /// <summary>总超时下限保护,避免传入 0 或负值导致立即放弃。</summary>
    internal TimeSpan EffectiveTimeout => Timeout > TimeSpan.Zero ? Timeout : TimeSpan.FromSeconds(8);

    /// <summary>单个协议发现器可用的时间预算(略短于总超时,保证能对外返回部分结果)。</summary>
    internal TimeSpan ProviderTimeout(TimeSpan share)
    {
        var budget = EffectiveTimeout - TimeSpan.FromMilliseconds(250);
        if (budget <= TimeSpan.Zero)
        {
            budget = EffectiveTimeout;
        }

        return share < budget ? share : budget;
    }
}

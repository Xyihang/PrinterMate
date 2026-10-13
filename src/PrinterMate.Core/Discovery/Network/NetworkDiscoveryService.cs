using System.Net;

namespace PrinterMate.Core.Discovery;

/// <summary>局域网设备发现的统一入口:并发运行全部发现器,汇总去重,并支持手动目标探测。</summary>
public sealed class NetworkDiscoveryService
{
    private readonly List<INetworkDiscovery> _providers;
    private readonly IppProbe _probe;

    public NetworkDiscoveryService(IEnumerable<INetworkDiscovery>? providers = null)
    {
        _providers = providers?.ToList() ?? [new SnmpDiscovery(), new MdnsDiscovery(), new WsdDiscovery()];
        _probe = new IppProbe();
    }

    /// <summary>当前注册的发现器中文名。</summary>
    public IReadOnlyList<string> ProviderNames => _providers.Select(p => p.Name).ToList();

    /// <summary>并发执行全部发现器,汇总去重;总耗时不超过 options.Timeout。</summary>
    public async Task<OperationResult<IReadOnlyList<DiscoveredPrinter>>> DiscoverAllAsync(
        DiscoveryOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        options ??= DiscoveryOptions.Default;
        var active = _providers.Where(p => IsEnabled(p, options)).ToList();
        if (active.Count == 0)
        {
            return OperationResult<IReadOnlyList<DiscoveredPrinter>>.Ok([], "没有启用任何网络发现方式。");
        }

        var collected = new List<DiscoveredPrinter>();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(options.EffectiveTimeout);

            var tasks = new List<Task<IReadOnlyList<DiscoveredPrinter>>>(active.Count);
            foreach (var provider in active)
            {
                tasks.Add(RunProviderAsync(provider, options, progress, linked.Token));
            }

            var all = Task.WhenAll(tasks);
            // 即便某个发现器忽略取消令牌,整体也在总超时处收口,绝不卡死。
            var guard = Task.Delay(options.EffectiveTimeout);
            var winner = await Task.WhenAny(all, guard).ConfigureAwait(false);

            if (winner == all)
            {
                foreach (var batch in await all.ConfigureAwait(false))
                {
                    collected.AddRange(batch);
                }
            }
            else
            {
                foreach (var task in tasks)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        collected.AddRange(task.Result);
                    }
                }

                Observe(all);
            }
        }
        catch (Exception)
        {
            // 单个发现器异常不会中断整体流程,返回已收集到的结果。
        }

        if (ct.IsCancellationRequested)
        {
            return OperationResult<IReadOnlyList<DiscoveredPrinter>>.Fail(
                "网络发现已取消。",
                null,
                OperationStatus.Cancelled);
        }

        progress?.Report("正在汇总发现结果 …");
        var merged = DiscoveredPrinterMerger.Merge(collected);
        return OperationResult<IReadOnlyList<DiscoveredPrinter>>.Ok(
            merged,
            merged.Count == 0 ? "未发现可用的网络打印机。" : $"共发现 {merged.Count} 台网络打印设备。");
    }

    /// <summary>手动输入 IP/主机名后的探测。</summary>
    public async Task<OperationResult<DiscoveredPrinter>> DiscoverHostAsync(
        string hostOrIp,
        DiscoveryOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= DiscoveryOptions.Default;

        if (string.IsNullOrWhiteSpace(hostOrIp))
        {
            return OperationResult<DiscoveredPrinter>.Fail(
                "请输入要探测的 IP 地址或主机名。",
                "例如 192.168.1.20 或 printer.local。");
        }

        var target = hostOrIp.Trim();

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(options.EffectiveTimeout);

            var printer = await _probe.ProbeHostAsync(target, options, linked.Token).ConfigureAwait(false);
            if (printer is not null)
            {
                return OperationResult<DiscoveredPrinter>.Ok(printer, $"已连接到 {printer.DisplayName}。");
            }

            return OperationResult<DiscoveredPrinter>.Fail(
                $"未在 {target} 上发现可用的打印服务。",
                "请确认设备已开机并与本机处于同一网段,且放行 631(IPP)、9100(RAW)、515(LPD)端口。",
                OperationStatus.NeedsUserAction);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return OperationResult<DiscoveredPrinter>.Fail("探测已取消。", null, OperationStatus.Cancelled);
        }
        catch (Exception ex)
        {
            return OperationResult<DiscoveredPrinter>.Fail(
                $"探测 {target} 失败。",
                "请检查网络连通性后重试,或改用其它 IP。",
                OperationStatus.Failed,
                ex);
        }
    }

    /// <summary>列出本机所有可用的 IPv4 网段/广播地址(用于定向广播)。</summary>
    public static IReadOnlyList<IPAddress> GetLocalBroadcastAddresses() =>
        NetworkAddresses.GetLocalBroadcastAddresses();

    private static async Task<IReadOnlyList<DiscoveredPrinter>> RunProviderAsync(
        INetworkDiscovery provider,
        DiscoveryOptions options,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report($"正在执行 {provider.Name} …");
        try
        {
            var result = await provider.DiscoverAsync(options, ct).ConfigureAwait(false);
            return result ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static bool IsEnabled(INetworkDiscovery provider, DiscoveryOptions options) => provider switch
    {
        SnmpDiscovery => options.EnableSnmp,
        MdnsDiscovery => options.EnableMdns,
        WsdDiscovery => options.EnableWsd,
        _ => true,
    };
}

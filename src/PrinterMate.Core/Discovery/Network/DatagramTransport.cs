using System.Net;
using System.Net.Sockets;

namespace PrinterMate.Core.Discovery;

/// <summary>
/// UDP 单播/组播/广播收发小工具。所有网络异常在此处收敛,调用方永远不会看到异常。
/// </summary>
internal static class DatagramTransport
{
    /// <summary>向若干目标发送同一份报文,并在 <paramref name="timeout"/> 内持续收取响应。</summary>
    public static async Task SendAndReceiveAsync(
        byte[] payload,
        IReadOnlyList<IPEndPoint> targets,
        IPEndPoint? bindTo,
        IPAddress? multicastGroup,
        TimeSpan timeout,
        Action<byte[], IPEndPoint> onResponse,
        CancellationToken ct)
    {
        if (targets.Count == 0 || payload.Length == 0)
        {
            return;
        }

        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            Bind(socket, bindTo);

            if (multicastGroup is not null)
            {
                try
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(multicastGroup));
                }
                catch (SocketException)
                {
                    // 无可用组播网卡时静默降级为仅收单播应答。
                }
            }

            foreach (var target in targets)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await socket.SendToAsync(payload, SocketFlags.None, target, ct).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // 某个网段不可达不影响其它网段。
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            await ReceiveLoopAsync(socket, timeout, onResponse, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 离线、无网卡、端口被占用等一律静默返回,由调用方给出空结果。
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private static void Bind(Socket socket, IPEndPoint? bindTo)
    {
        try
        {
            socket.Bind(bindTo ?? new IPEndPoint(IPAddress.Any, 0));
        }
        catch (SocketException)
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
    }

    private static async Task ReceiveLoopAsync(
        Socket socket,
        TimeSpan timeout,
        Action<byte[], IPEndPoint> onResponse,
        CancellationToken ct)
    {
        var buffer = new byte[65535];
        var remote = new IPEndPoint(IPAddress.Any, 0);
        var deadline = Task.Delay(timeout);

        while (!ct.IsCancellationRequested)
        {
            var receive = socket
                .ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, remote, ct)
                .AsTask();

            var completed = await Task.WhenAny(receive, deadline).ConfigureAwait(false);
            if (completed != receive)
            {
                Observe(receive);
                return;
            }

            SocketReceiveFromResult result;
            try
            {
                result = await receive.ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (result.ReceivedBytes <= 0)
            {
                continue;
            }

            var payload = new byte[result.ReceivedBytes];
            Buffer.BlockCopy(buffer, 0, payload, 0, result.ReceivedBytes);
            try
            {
                onResponse(payload, (IPEndPoint)result.RemoteEndPoint);
            }
            catch (Exception)
            {
                // 单个坏包不中断整体发现。
            }
        }
    }

    /// <summary>把被放弃的接收任务的异常标记为已观察,避免未观察异常告警。</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

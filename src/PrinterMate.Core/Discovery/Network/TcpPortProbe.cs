using System.Net;
using System.Net.Sockets;

namespace PrinterMate.Core.Discovery;

/// <summary>短超时 TCP 端口探测。失败一律返回 false,不抛异常。</summary>
internal static class TcpPortProbe
{
    public const int IppPort = 631;
    public const int RawPort = 9100;
    public const int LpdPort = 515;

    public static Task<bool> IsOpenAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct) =>
        ProbeAsync(address.ToString(), address, port, timeout, ct);

    public static Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct) =>
        ProbeAsync(host, null, port, timeout, ct);

    private static async Task<bool> ProbeAsync(string host, IPAddress? address, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new TcpClient
        {
            NoDelay = true,
        };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(800) : timeout);

        try
        {
            var connect = address is not null
                ? client.ConnectAsync(address, port, linked.Token)
                : client.ConnectAsync(host, port, linked.Token);

            await connect.ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // 忽略关闭异常。
            }
        }
    }
}

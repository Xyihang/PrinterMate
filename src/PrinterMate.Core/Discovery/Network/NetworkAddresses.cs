using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PrinterMate.Core.Discovery;

/// <summary>本机 IPv4 网络地址推导。纯本地操作,无网络依赖,永不抛异常。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class NetworkAddresses
{
    /// <summary>列出本机所有已启用网卡的 IPv4 定向广播地址(如 192.168.1.255)。</summary>
    public static IReadOnlyList<IPAddress> GetLocalBroadcastAddresses()
    {
        var results = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPInterfaceProperties properties;
                try
                {
                    properties = nic.GetIPProperties();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var mask = unicast.IPv4Mask;
                    if (mask is null)
                    {
                        continue;
                    }

                    var broadcast = ComputeBroadcast(unicast.Address, mask);
                    if (broadcast is not null && !results.Contains(broadcast))
                    {
                        results.Add(broadcast);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 网络栈异常时返回空列表。
        }

        return results;
    }

    /// <summary>由 IP 与子网掩码计算定向广播地址。</summary>
    public static IPAddress? ComputeBroadcast(IPAddress address, IPAddress mask)
    {
        if (address is null || mask is null)
        {
            return null;
        }

        var ipBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (ipBytes.Length != 4 || maskBytes.Length != 4)
        {
            return null;
        }

        var broadcast = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            broadcast[i] = (byte)(ipBytes[i] | (byte)~maskBytes[i]);
        }

        return new IPAddress(broadcast);
    }
}

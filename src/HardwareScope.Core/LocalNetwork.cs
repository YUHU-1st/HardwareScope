using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HardwareScope.Core;

internal static class LocalNetwork
{
    public static bool IsLocalPeer(IPAddress? address)
    {
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var peer = address.GetAddressBytes();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var local = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                if (mask.All(value => value == 0)) continue;
                if (Enumerable.Range(0, 4).All(i => (peer[i] & mask[i]) == (local[i] & mask[i]))) return true;
            }
        }
        return false;
    }
}

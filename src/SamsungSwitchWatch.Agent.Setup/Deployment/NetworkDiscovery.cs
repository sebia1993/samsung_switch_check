using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SamsungSwitchWatch.Agent.Setup.Deployment;

public sealed class WindowsNetworkDiscovery : INetworkDiscovery
{
    public IReadOnlyList<NetworkCandidate> DiscoverPrivateIpv4Networks()
    {
        var addresses = new List<NetworkAddress>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is
                    NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null)
                {
                    continue;
                }

                addresses.Add(new NetworkAddress(
                    networkInterface.Id,
                    networkInterface.Name,
                    networkInterface.Description,
                    unicast.Address,
                    unicast.IPv4Mask));
            }
        }

        return BuildCandidates(addresses);
    }

    internal static IReadOnlyList<NetworkCandidate> BuildCandidates(
        IEnumerable<NetworkAddress> addresses)
    {
        var results = new Dictionary<string, NetworkCandidate>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            if (!Ipv4Input.IsPrivate(address.Address) ||
                !TryGetPrefix(address.Mask, out var prefix))
            {
                continue;
            }

            var network = ApplyMask(address.Address, address.Mask);
            if (!Ipv4Input.IsPrivateNetwork(network, prefix))
            {
                continue;
            }

            var cidr = $"{network}/{prefix}";
            var id = $"{address.InterfaceId}:{address.Address}";
            results.TryAdd(
                id,
                new NetworkCandidate(
                    id,
                    address.InterfaceName,
                    address.Address.ToString(),
                    cidr,
                    address.Description));
        }

        return results.Values
            .OrderBy(candidate => candidate.InterfaceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Cidr, StringComparer.Ordinal)
            .ToArray();
    }

    private static IPAddress ApplyMask(IPAddress address, IPAddress mask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var network = new byte[4];
        for (var index = 0; index < network.Length; index++)
        {
            network[index] = (byte)(addressBytes[index] & maskBytes[index]);
        }

        return new IPAddress(network);
    }

    private static bool TryGetPrefix(IPAddress mask, out int prefix)
    {
        prefix = 0;
        var zeroSeen = false;
        foreach (var value in mask.GetAddressBytes())
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var set = (value & (1 << bit)) != 0;
                if (set && zeroSeen)
                {
                    prefix = 0;
                    return false;
                }

                if (set)
                {
                    prefix++;
                }
                else
                {
                    zeroSeen = true;
                }
            }
        }

        return true;
    }
}

internal sealed record NetworkAddress(
    string InterfaceId,
    string InterfaceName,
    string Description,
    IPAddress Address,
    IPAddress Mask);

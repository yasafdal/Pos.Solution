using System.Net;
using System.Net.Sockets;

namespace POS.Client.Services;

// Runs in the native MAUI process, not in the API or the WebView's JavaScript.
public class TerminalAddressProvider(HttpClient http) : ITerminalAddressProvider
{
#if DEBUG
    // Explicitly configured by the development host; absent from Release builds.
    public string? DevelopmentAddressOverride { get; init; }
#endif

    public async Task<string> GetAddressAsync()
    {
#if DEBUG
        if (DevelopmentAddressOverride != null)
        {
            // Apply the same address rules as the authenticated outlet lookup.
            if (!IPAddress.TryParse(DevelopmentAddressOverride, out var address))
                throw new InvalidOperationException("A valid unicast development terminal IP address is required.");
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)
                || address.IsIPv6Multicast
                || (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] >= 224))
                throw new InvalidOperationException("A valid unicast development terminal IP address is required.");
            return address.ToString();
        }
#endif
        var endpoint = http.BaseAddress ?? throw new InvalidOperationException("The POS API address is not configured.");
        var destinations = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost);
        return await ResolveAsync(destinations, endpoint.Port, GetRouteAddressAsync);
    }

    public static async Task<string> ResolveAsync(IEnumerable<IPAddress> destinations, int port,
        Func<IPAddress, int, Task<IPAddress?>> routeAddress)
    {
        var addresses = new HashSet<IPAddress>();
        foreach (var destination in destinations)
        {
            if (IPAddress.IsLoopback(destination)) continue;
            try
            {
                var local = await routeAddress(destination, port);
                if (local != null && !IPAddress.IsLoopback(local)
                    && !local.Equals(IPAddress.Any) && !local.Equals(IPAddress.IPv6Any))
                    addresses.Add(local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local);
            }
            catch (SocketException) { /* Another DNS destination may have a usable route. */ }
        }
        if (addresses.Count != 1)
            throw new InvalidOperationException("Cannot determine a unique LAN IP for this terminal. Configure the POS API with a reachable LAN address (not localhost) and check the network route.");
        return addresses.Single().ToString();
    }

    private static async Task<IPAddress?> GetRouteAddressAsync(IPAddress destination, int port)
    {
        // UDP connect selects the OS route/local endpoint without sending a packet.
        using var socket = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.ConnectAsync(new IPEndPoint(destination, port));
        return (socket.LocalEndPoint as IPEndPoint)?.Address;
    }
}

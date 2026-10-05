using System.Net;
using System.Net.Sockets;

namespace SolarManagement.Http;

/// <summary>Pins each connection to DNS results validated as public before credentials leave the process.</summary>
public static class PublicHttpTransport
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5), ConnectCallback = ConnectAsync
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !PublicNetworkAddressPolicy.IsPublic(address)))
            throw new HttpRequestException("The endpoint did not resolve exclusively to public addresses.");
        foreach (var address in addresses)
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket?.Dispose(); }
            catch { socket?.Dispose(); throw; }
        }
        throw new HttpRequestException("The public endpoint connection failed.");
    }
}

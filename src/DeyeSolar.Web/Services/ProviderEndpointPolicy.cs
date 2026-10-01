using System.Net;
using System.Net.Sockets;

namespace DeyeSolar.Web.Services;

/// <summary>Credential-bearing provider probes can only address the provider's HTTPS hosts.</summary>
public static class ProviderEndpointPolicy
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5), ConnectCallback = ConnectAsync
    };
    public static bool TryDeye(string? value, out Uri uri)
    {
        uri = null!;
        if (!TryHttps(value, out var candidate)
            || candidate.Host is not ("eu1-developer.deyecloud.com" or "us1-developer.deyecloud.com")
            || candidate.AbsolutePath.TrimEnd('/') != "/v1.0") return false;
        uri = new Uri(candidate.GetLeftPart(UriPartial.Authority) + "/v1.0/");
        return true;
    }

    public static bool TryShelly(string? value, out Uri uri)
    {
        uri = null!;
        var text = value?.Trim() ?? "";
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!TryHttps(text, out var candidate) || !candidate.Host.EndsWith(".shelly.cloud", StringComparison.Ordinal)
            || candidate.AbsolutePath != "/") return false;
        uri = new Uri(candidate.GetLeftPart(UriPartial.Authority) + "/");
        return true;
    }

    private static bool TryHttps(string? value, out Uri uri) => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out uri!)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.HostNameType == UriHostNameType.Dns;

    public static bool IsPublicAddress(IPAddress value)
    {
        if (value.IsIPv4MappedToIPv6) value = value.MapToIPv4();
        if (IPAddress.IsLoopback(value)) return false;
        var b = value.GetAddressBytes();
        if (value.AddressFamily == AddressFamily.InterNetwork)
            return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224
                && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127)
                && !(b[0] == 198 && b[1] is 18 or 19)
                && !(b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                && !(b[0] == 192 && b[1] == 88 && b[2] == 99)
                && !(b[0] == 198 && b[1] == 51 && b[2] == 100)
                && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        // IANA special-purpose prefixes are not provider origins. Exclude protocol/transition
        // ranges as well as documentation addresses, even when inside global unicast space.
        return value.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20
            && !(b[0] == 0x20 && b[1] == 0x01 && b[2] < 2) // 2001::/23, including Teredo
            && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
            && !(b[0] == 0x20 && b[1] == 0x02) // 6to4 can embed a non-public IPv4 destination
            && !(b[0] == 0x3f && b[1] == 0xff && b[2] < 16);
    }

    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("Provider address is not public.");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Provider connection failed.");
    }
}

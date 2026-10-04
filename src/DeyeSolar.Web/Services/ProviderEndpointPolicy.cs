using System.Net;
using System.Net.Sockets;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Services;

/// <summary>Credential-bearing provider probes can only address the provider's HTTPS hosts.</summary>
public static class ProviderEndpointPolicy
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = ConnectAsync
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
        => PublicNetworkAddressPolicy.IsPublic(value);

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

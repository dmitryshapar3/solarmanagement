using System.Net;
using SolarManagement.Http;

namespace SolarManagement.Integrations.WorkerSdk;

public static class CloudHttpClient
{
    public static string ValidateEndpoint(string value, IReadOnlyList<string> origins, string requiredPath,
        bool allowMissingScheme = false)
    {
        var text = value.Trim();
        if (allowMissingScheme && !text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.HostNameType != UriHostNameType.Dns
            || !origins.Any(origin => Matches(uri, origin))) throw new ArgumentException("The cloud endpoint is not authorized.");
        var path = uri.AbsolutePath.TrimEnd('/');
        if ((path.Length == 0 ? "/" : path) != requiredPath) throw new ArgumentException("The cloud API path is unsupported.");
        return uri.GetLeftPart(UriPartial.Authority) + requiredPath;
    }
    public static HttpClient Create(IReadOnlyList<string> origins)
    {
        var transport = PublicHttpTransport.CreateHandler();
        return new HttpClient(new OriginGuard(origins, transport)) { Timeout = TimeSpan.FromSeconds(30) };
    }
    internal sealed class OriginGuard(IReadOnlyList<string> origins, HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri;
            if (uri is null || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443
                || !origins.Any(origin => Matches(uri, origin))) throw new HttpRequestException("Cloud endpoint is not authorized.");
            var response = await base.SendAsync(request, ct);
            try
            {
                const int maximumBytes = 4 * 1024 * 1024;
                byte[] bytes;
                try { bytes = await BoundedHttpContent.ReadBytesAsync(response.Content, maximumBytes, ct); }
                catch (ResponseTooLargeException) { throw new InvalidDataException("Cloud response exceeded its allowed size."); }
                var bounded = new ByteArrayContent(bytes);
                foreach (var header in response.Content.Headers) bounded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content.Dispose();
                response.Content = bounded;
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }
    public static bool Matches(Uri uri, string origin)
    {
        if (origin.StartsWith("https://*.", StringComparison.Ordinal))
        {
            var suffix = origin[9..];
            return uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && uri.Host.Length > suffix.Length;
        }
        return Uri.TryCreate(origin, UriKind.Absolute, out var allowed) && allowed.Scheme == uri.Scheme
            && allowed.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase) && allowed.Port == uri.Port;
    }
    public static bool IsPublic(IPAddress address)
        => PublicNetworkAddressPolicy.IsPublic(address);
}

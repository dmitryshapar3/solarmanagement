using System.Security.Cryptography;
using System.Text;

namespace SolarManagement.Integrations.Contracts;

public static class IntegrationOAuthProtocol
{
    public static void ValidateBegin(IntegrationOAuthBeginRequest request)
    {
        ValidateRedirect(request.RedirectUri);
        if (request.CodeChallengeMethod != "S256" || request.State is not { Length: >= 32 and <= 128 }
            || !request.State.All(Base64UrlCharacter) || request.CodeChallenge is not { Length: 43 }
            || !request.CodeChallenge.All(Base64UrlCharacter))
            throw new ArgumentException("OAuth requires bounded state and an S256 PKCE challenge.");
    }
    public static void ValidateComplete(IntegrationOAuthCompleteRequest request)
    {
        ValidateRedirect(request.RedirectUri);
        if (request.Code is not { Length: >= 1 and <= 2048 } || request.Code.Any(char.IsControl)
            || request.CodeVerifier is not { Length: >= 43 and <= 128 }
            || !request.CodeVerifier.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~'))
            throw new ArgumentException("OAuth code or PKCE verifier is invalid.");
    }
    public static string Challenge(string verifier)
        => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static bool IsApprovedAuthorizationUrl(string? value, IReadOnlyList<string> allowedOrigins)
    {
        if (value is not { Length: >= 1 and <= 8192 } || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || Uri.CheckHostName(uri.Host) != UriHostNameType.Dns) return false;
        foreach (var origin in allowedOrigins)
        {
            if (origin.StartsWith("https://*.", StringComparison.Ordinal))
            {
                var suffix = origin[10..];
                if (Uri.CheckHostName(suffix) == UriHostNameType.Dns && uri.Host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (Uri.TryCreate(origin, UriKind.Absolute, out var approved) && approved.Scheme == Uri.UriSchemeHttps
                && approved.Port == 443 && approved.UserInfo.Length == 0 && approved.AbsolutePath == "/" && approved.Query.Length == 0
                && approved.Fragment.Length == 0 && string.Equals(uri.Host, approved.Host, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    public static void ValidateAuthorizationUrl(string value, IntegrationOAuthBeginRequest request, IReadOnlyList<string> allowedOrigins)
    {
        if (!IsApprovedAuthorizationUrl(value, allowedOrigins)) throw new InvalidDataException("OAuth authorization URL is outside the admitted provider origins.");
        var query = ParseQuery(new Uri(value).Query);
        if (!query.TryGetValue("state", out var state) || state != request.State
            || !query.TryGetValue("redirect_uri", out var redirect) || redirect != request.RedirectUri
            || !query.TryGetValue("code_challenge", out var challenge) || challenge != request.CodeChallenge
            || !query.TryGetValue("code_challenge_method", out var method) || method != "S256"
            || !query.TryGetValue("response_type", out var response) || response != "code")
            throw new InvalidDataException("OAuth authorization URL does not preserve the bound flow or PKCE challenge.");
    }
    private static Dictionary<string, string> ParseQuery(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var decoded = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            if (!result.TryAdd(key, decoded)) throw new InvalidDataException("OAuth authorization parameters must be unique.");
        }
        return result;
    }
    private static void ValidateRedirect(string? value)
    {
        if (value is not { Length: >= 1 and <= 2048 } || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new ArgumentException("OAuth callback must be an absolute HTTPS URL or a local development loopback URL.");
    }
    private static bool Base64UrlCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
}

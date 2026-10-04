namespace SolarManagement.Integrations.Contracts;

public static class IntegrationOriginPolicy
{
    public static string NormalizeExactOrigin(string origin)
    {
        if (origin is not { Length: >= 1 and <= 512 } || origin != origin.Trim()
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || Uri.CheckHostName(uri.IdnHost) != UriHostNameType.Dns || !uri.IdnHost.Contains('.')
            || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".", StringComparison.Ordinal)
            || uri.IdnHost.Length > 253 || uri.IdnHost.Split('.').Any(label => label.Length is < 1 or > 63
                || !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1])
                || !label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException("Approve an exact HTTPS public DNS origin without a path, wildcard, credentials or custom port.", nameof(origin));
        return "https://" + uri.IdnHost.ToLowerInvariant();
    }
}

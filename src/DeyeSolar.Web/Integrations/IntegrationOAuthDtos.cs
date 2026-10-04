using System.Text.Json;

namespace DeyeSolar.Web.Integrations;

public sealed record IntegrationOAuthStartRequest(IntegrationConfigurationChange Draft, string Client = "web");
public sealed record IntegrationOAuthStartDto(Guid FlowId, string AuthorizationUrl, DateTimeOffset ExpiresAt,
    string ReturnUri, string ReturnNonce);
public sealed record IntegrationOAuthStatusDto(Guid FlowId, string Status, DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyDictionary<string, bool> SecretPresent, string? Code = null);

public sealed class IntegrationOAuthOptions
{
    public string PublicBaseUrl { get; init; } = "https://solar.dshapar.com";
    public string CallbackUri => new Uri(new Uri(PublicBaseUrl), "/integrations/oauth/callback").AbsoluteUri;
    public const string MobileReturnUri = "deyesolar://integration-oauth";
    public void Validate()
    {
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var origin) || origin.Scheme != "https"
            || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/")
            throw new InvalidOperationException("The integration OAuth callback must use the operator's HTTPS public origin.");
    }
}

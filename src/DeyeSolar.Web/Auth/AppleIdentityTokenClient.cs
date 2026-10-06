using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeyeSolar.Web.Auth;

public sealed record AppleIdentityTokens(string IdentityToken, string RefreshToken);
public interface IAppleIdentityTokenClient
{
    Task<AppleIdentityTokens> ExchangeAsync(string code, string audience, string? redirectUri, CancellationToken ct);
    Task RevokeAsync(string refreshToken, string audience, CancellationToken ct);
}
public sealed class AppleIdentityTokenClient(IHttpClientFactory clients, AuthProviderOptions providers, TimeProvider clock) : IAppleIdentityTokenClient
{
    public async Task<AppleIdentityTokens> ExchangeAsync(string code, string audience, string? redirectUri, CancellationToken ct)
    {
        if (code is not { Length: > 0 and <= 4000 }) throw new AccountIdentityException("apple_failed", "Apple authorization expired. Start again.");
        var form = new Dictionary<string, string> { ["client_id"] = audience, ["client_secret"] = ClientSecret(audience), ["code"] = code, ["grant_type"] = "authorization_code" };
        if (redirectUri is not null) form["redirect_uri"] = redirectUri;
        using var response = await clients.CreateClient(AppleIdentityVerifier.ClientName).PostAsync("https://appleid.apple.com/auth/token", new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode) throw new AccountIdentityException("apple_failed", "Apple authorization expired. Start again.");
        using var document = JsonDocument.Parse(await SolarManagement.Http.BoundedHttpContent.ReadBytesAsync(response.Content, 64 * 1024, ct));
        var root = document.RootElement;
        var identity = root.TryGetProperty("id_token", out var id) ? id.GetString() : null;
        var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        if (identity is not { Length: > 0 and <= 20000 } || refresh is not { Length: > 0 and <= 20000 }) throw new AccountIdentityException("apple_failed", "Apple authorization could not be saved safely.");
        return new(identity, refresh);
    }
    public async Task RevokeAsync(string refreshToken, string audience, CancellationToken ct)
    {
        using var response = await clients.CreateClient(AppleIdentityVerifier.ClientName).PostAsync("https://appleid.apple.com/auth/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = audience, ["client_secret"] = ClientSecret(audience), ["token"] = refreshToken, ["token_type_hint"] = "refresh_token" }), ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Apple token revocation is temporarily unavailable.");
    }
    private string ClientSecret(string audience)
    {
        var options = providers.Apple;
        if (!options.NativeAvailable || audience != options.NativeClientId && audience != options.ServicesId) throw new AccountIdentityException("apple_unavailable", "Apple sign-in is unavailable.");
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var header = AppleIdentityVerifier.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = options.KeyId }));
        var payload = AppleIdentityVerifier.Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = options.TeamId, iat = now, exp = now + 300, aud = "https://appleid.apple.com", sub = audience }));
        using var key = ECDsa.Create(); key.ImportFromPem(options.PrivateKeyPem);
        var unsigned = header + "." + payload;
        return unsigned + "." + AppleIdentityVerifier.Encode(key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
}

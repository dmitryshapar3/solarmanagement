using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeyeSolar.Web.Auth;

public sealed record VerifiedAppleIdentity(string Subject, string? Email, bool EmailVerified);
public interface IAppleIdentityVerifier
{
    Task<VerifiedAppleIdentity> VerifyAsync(string token, string rawNonce, string audience, CancellationToken ct);
}
public sealed class AppleIdentityVerifier(IHttpClientFactory clients, TimeProvider clock) : IAppleIdentityVerifier
{
    public const string ClientName = "AppleIdentity";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, RSAParameters> _keys = new(StringComparer.Ordinal);
    private DateTimeOffset _loadedAt;
    public async Task<VerifiedAppleIdentity> VerifyAsync(string token, string rawNonce, string audience, CancellationToken ct)
    {
        try
        {
            if (token is not { Length: > 10 and <= 20000 } || rawNonce is not { Length: >= 16 and <= 256 } || audience is not { Length: > 0 and <= 255 }) throw Denied();
            var parts = token.Split('.');
            if (parts.Length != 3) throw Denied();
            using var header = JsonDocument.Parse(Decode(parts[0]));
            using var payload = JsonDocument.Parse(Decode(parts[1]));
            DeyeSolar.Web.Billing.AppleSignedDataVerifier.RejectDuplicateProperties(header.RootElement);
            DeyeSolar.Web.Billing.AppleSignedDataVerifier.RejectDuplicateProperties(payload.RootElement);
            if (header.RootElement.GetProperty("alg").GetString() != "RS256") throw Denied();
            var kid = header.RootElement.GetProperty("kid").GetString() ?? "";
            var key = await KeyAsync(kid, ct);
            using var rsa = RSA.Create(); rsa.ImportParameters(key);
            if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw Denied();
            var claims = payload.RootElement;
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            var exp = claims.GetProperty("exp").GetInt64(); var iat = claims.GetProperty("iat").GetInt64();
            var nonce = claims.GetProperty("nonce").GetString() ?? "";
            var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawNonce))).ToLowerInvariant();
            if (claims.GetProperty("iss").GetString() != "https://appleid.apple.com" || claims.GetProperty("aud").GetString() != audience
                || exp <= now || iat > now + 60 || iat < now - 3600 || exp < iat || !FixedEquals(nonce, expected)) throw Denied();
            var subject = claims.GetProperty("sub").GetString();
            if (subject is not { Length: > 0 and <= 255 } || subject.Any(char.IsControl)) throw Denied();
            var verified = claims.TryGetProperty("email_verified", out var flag) && (flag.ValueKind == JsonValueKind.True || flag.ValueKind == JsonValueKind.String && flag.GetString() == "true");
            var email = claims.TryGetProperty("email", out var emailClaim) && emailClaim.ValueKind == JsonValueKind.String ? emailClaim.GetString() : null;
            var validEmail = verified && email is not null && OneTimeVerificationService.TryNormalize("email", email, out var normalized);
            return new(subject, validEmail ? email!.Trim().ToLowerInvariant() : null, validEmail);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or CryptographicException or InvalidOperationException or OverflowException or DeyeSolar.Web.Billing.AppleBillingException)
        { throw Denied(); }
    }
    private async Task<RSAParameters> KeyAsync(string kid, CancellationToken ct)
    {
        if (kid.Length is < 1 or > 100) throw Denied();
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (now - _loadedAt > TimeSpan.FromHours(1) || !_keys.ContainsKey(kid) && now - _loadedAt > TimeSpan.FromMinutes(1))
            {
                using var response = await clients.CreateClient(ClientName).GetAsync("https://appleid.apple.com/auth/keys", ct);
                response.EnsureSuccessStatusCode();
                var body = await SolarManagement.Http.BoundedHttpContent.ReadBytesAsync(response.Content, 128 * 1024, ct);
                using var document = JsonDocument.Parse(body);
                var keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
                foreach (var jwk in document.RootElement.GetProperty("keys").EnumerateArray().Take(20))
                {
                    if (jwk.GetProperty("kty").GetString() != "RSA" || jwk.GetProperty("alg").GetString() != "RS256" || jwk.GetProperty("use").GetString() != "sig") continue;
                    keys[jwk.GetProperty("kid").GetString()!] = new() { Modulus = Decode(jwk.GetProperty("n").GetString()!), Exponent = Decode(jwk.GetProperty("e").GetString()!) };
                }
                _keys = keys; _loadedAt = now;
            }
            return _keys.TryGetValue(kid, out var found) ? found : throw Denied();
        }
        finally { _gate.Release(); }
    }
    internal static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    internal static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private static AccountIdentityException Denied() => new("apple_failed", "Apple did not provide a valid verified identity.");
}

using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Web.Auth;
using Microsoft.AspNetCore.Identity;

namespace DeyeSolar.Web.Tests;

public sealed class AppleIdentityProtocolTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(string jwks) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("https://appleid.apple.com/auth/keys", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jwks) });
        }
    }
    private sealed class Clients(Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }
    private static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Token(RSA key, Clock clock, string nonce, string audience = "com.dshapar.solar", string? extra = null)
    {
        var header = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"kid\":\"fixture-key\"}"));
        var payload = Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { iss = "https://appleid.apple.com", aud = audience, sub = "apple-subject",
            exp = clock.Now.AddMinutes(5).ToUnixTimeSeconds(), iat = clock.Now.ToUnixTimeSeconds(), nonce = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant(), email = "owner@example.test", email_verified = "true" }).TrimEnd('}') + (extra ?? "") + "}"));
        var unsigned = header + "." + payload;
        return unsigned + "." + Encode(key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    [Fact]
    public async Task AppleJwtRequiresSignedAudienceNonceAndFreshDatesAndRejectsDuplicateClaims()
    {
        using var rsa = RSA.Create(2048); var key = rsa.ExportParameters(false); var clock = new Clock();
        var handler = new Handler(JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", alg = "RS256", use = "sig", kid = "fixture-key", n = Encode(key.Modulus!), e = Encode(key.Exponent!) } } }));
        var verifier = new AppleIdentityVerifier(new Clients(handler), clock); const string nonce = "nonce-long-enough-for-security-123";
        var valid = Token(rsa, clock, nonce);
        Assert.Equal(new VerifiedAppleIdentity("apple-subject", "owner@example.test", true), await verifier.VerifyAsync(valid, nonce, "com.dshapar.solar", default));
        await Assert.ThrowsAsync<AccountIdentityException>(() => verifier.VerifyAsync(valid, "another-long-random-nonce", "com.dshapar.solar", default));
        await Assert.ThrowsAsync<AccountIdentityException>(() => verifier.VerifyAsync(valid, nonce, "other.audience", default));
        await Assert.ThrowsAsync<AccountIdentityException>(() => verifier.VerifyAsync(Token(rsa, clock, nonce, extra: ",\"aud\":\"com.dshapar.solar\""), nonce, "com.dshapar.solar", default));
        using var foreign = RSA.Create(2048);
        await Assert.ThrowsAsync<AccountIdentityException>(() => verifier.VerifyAsync(Token(foreign, clock, nonce), nonce, "com.dshapar.solar", default));
        clock.Now = clock.Now.AddMinutes(6);
        await Assert.ThrowsAsync<AccountIdentityException>(() => verifier.VerifyAsync(valid, nonce, "com.dshapar.solar", default));
        Assert.Equal(1, handler.Calls);
    }
    private static ClaimsPrincipal Actor(string userId, string token, string stamp = "stamp") => new(new ClaimsIdentity(new[] {
        new Claim(ClaimTypes.NameIdentifier, userId), new Claim(InstallationAccessAuthorizer.SessionClaim, token), new Claim(InstallationAccessAuthorizer.StampClaim, stamp) }, "fixture"));
    [Fact]
    public async Task ExternalProofIsUserSessionStampOperationBoundAndConsumedOnce()
    {
        var clock = new Clock(); var store = new ExternalAccountProofStore(clock); var user = new IdentityUser { Id = "owner", SecurityStamp = "stamp" };
        var actor = Actor("owner", "session-one"); var flow = store.Start(actor, user, "Google", "delete");
        Assert.Throws<AccountSecurityException>(() => store.Complete(flow.Id, "Google", "other"));
        var id = store.Complete(flow.Id, "Google", user.Id);
        Assert.False(store.Consume(id, Actor("owner", "session-two"), user, "delete"));
        Assert.False(store.Consume(id, actor, user, "export"));
        Assert.False(store.Consume(id, actor, new IdentityUser { Id = "owner", SecurityStamp = "changed" }, "delete"));
        Assert.True(store.Consume(id, actor, user, "delete")); Assert.False(store.Consume(id, actor, user, "delete"));
        var expired = store.Complete(store.Start(actor, user, "Apple", "export").Id, "Apple", user.Id);
        clock.Now = clock.Now.AddMinutes(3); Assert.False(store.Consume(expired, actor, user, "export"));
        Assert.Throws<AccountSecurityException>(() => store.Start(actor, user, "Other", "delete"));
    }
    [Fact]
    public void AppleBrowserCorrelationIsRequiredAndReplayAndUnsafeReturnsAreDenied()
    {
        var clock = new Clock(); var store = new AppleIdentityFlowStore(clock);
        var flow = store.StartWeb("private-cookie", "//evil.test", null);
        Assert.Equal("/", flow.ReturnUrl); Assert.Null(store.TakeWeb(flow.Id, "wrong-cookie"));
        Assert.Equal(flow, store.TakeWeb(flow.Id, "private-cookie")); Assert.Null(store.TakeWeb(flow.Id, "private-cookie"));
        foreach (var bad in new[] { "https://evil.test", "/\\evil.test", "/path\r\nLocation:bad", "//evil.test" }) Assert.Equal("/", AppleIdentityFlowStore.SafeReturnUrl(bad));
        Assert.Equal("/account?linked=apple", AppleIdentityFlowStore.SafeReturnUrl("/account?linked=apple"));
    }
    [Fact]
    public void AppleAvailabilityRequiresSeparateValidOperatorKeyAndServicesId()
    {
        Assert.False(new AuthAppleOptions().NativeAvailable);
        Assert.False(new AuthAppleOptions { Enabled = true, TeamId = "TEAM123456", KeyId = "KEY1234567", PrivateKeyPem = "bad PRIVATE KEY" }.NativeAvailable);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = new AuthAppleOptions { Enabled = true, TeamId = "TEAM123456", KeyId = "KEY1234567", PrivateKeyPem = key.ExportPkcs8PrivateKeyPem() };
        Assert.True(options.NativeAvailable); Assert.False(options.WebAvailable);
    }
}

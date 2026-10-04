using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Integrations.Tests;

public sealed class OAuthProtocolTests
{
    [Theory]
    [InlineData("https://oauth.example.test/authorize", true)]
    [InlineData("https://oauth.example.test.evil.example/authorize", false)]
    [InlineData("http://oauth.example.test/authorize", false)]
    [InlineData("https://oauth.example.test:444/authorize", false)]
    [InlineData("https://user@oauth.example.test/authorize", false)]
    [InlineData("https://oauth.example.test/authorize#fragment", false)]
    [InlineData("https://127.0.0.1/authorize", false)]
    [InlineData("https://foreign.example.test/authorize", false)]
    public void AuthorizationUrlsRequireExactAdmittedPublicHttpsOrigin(string url, bool expected)
        => Assert.Equal(expected, IntegrationOAuthProtocol.IsApprovedAuthorizationUrl(url, ["https://oauth.example.test"]));
    [Fact]
    public void AuthorizationUrlCannotChangeStateRedirectOrPkceAndCannotDuplicateParameters()
    {
        var verifier = new string('v', 64);
        var request = new IntegrationOAuthBeginRequest("https://host.example.test/callback", new string('s', 43), IntegrationOAuthProtocol.Challenge(verifier));
        IntegrationOAuthProtocol.ValidateBegin(request);
        IntegrationOAuthProtocol.ValidateComplete(new("authorization-code", request.RedirectUri, verifier));
        var url = "https://oauth.example.test/authorize?response_type=code&state=" + request.State
            + "&redirect_uri=" + Uri.EscapeDataString(request.RedirectUri) + "&code_challenge=" + request.CodeChallenge + "&code_challenge_method=S256";
        IntegrationOAuthProtocol.ValidateAuthorizationUrl(url, request, ["https://oauth.example.test"]);
        foreach (var changed in new[] { url.Replace(request.State, new string('x', 43)), url.Replace("S256", "plain"), url.Replace("response_type=code", "response_type=token"), url + "&state=" + request.State, url.Replace("host.example.test", "foreign.example.test") })
            Assert.Throws<InvalidDataException>(() => IntegrationOAuthProtocol.ValidateAuthorizationUrl(changed, request, ["https://oauth.example.test"]));
        Assert.Throws<ArgumentException>(() => IntegrationOAuthProtocol.ValidateBegin(request with { State = "short" }));
        Assert.Throws<ArgumentException>(() => IntegrationOAuthProtocol.ValidateBegin(request with { CodeChallengeMethod = "plain" }));
        Assert.Throws<ArgumentException>(() => IntegrationOAuthProtocol.ValidateComplete(new("code", request.RedirectUri, "short")));
    }
    [Fact]
    public void PkceUsesTheStandardS256TestVector()
        => Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", IntegrationOAuthProtocol.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    [Theory]
    [InlineData("http://api.example.test")]
    [InlineData("https://*.example.test")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://localhost")]
    [InlineData("https://api.localhost")]
    [InlineData("https://api.local")]
    [InlineData("https://api.example.test:444")]
    [InlineData("https://user@api.example.test")]
    [InlineData("https://api.example.test/path")]
    [InlineData("https://api.example.test?token=value")]
    [InlineData("https://api.example.test#fragment")]
    [InlineData("https://api.example.test.")]
    [InlineData("https://_api.example.test")]
    public void LiveOriginApprovalRejectsBroadLocalOrNonOriginValues(string origin)
        => Assert.Throws<ArgumentException>(() => IntegrationOriginPolicy.NormalizeExactOrigin(origin));
}

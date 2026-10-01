using System.Security.Cryptography;
using System.Text;
using DeyeSolar.Web.Auth;
using Microsoft.AspNetCore.WebUtilities;

namespace DeyeSolar.Web.Tests;

public class IdentityProofTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Delivery : IIdentityVerificationDelivery
    {
        public string Code = "";
        public int Sent;
        public bool Fail;
        public int PhoneChecks;
        public Task SendEmailAsync(string destination, string code, CancellationToken ct)
        { Sent++; Code = code; return Fail ? Task.FromException(new Exception("private provider response")) : Task.CompletedTask; }
        public Task SendPhoneAsync(string destination, CancellationToken ct) { Sent++; return Task.CompletedTask; }
        public Task<bool> CheckPhoneAsync(string destination, string code, CancellationToken ct)
        { PhoneChecks++; return Task.FromResult(code == "654321"); }
    }
    private static AuthProviderOptions Enabled => new()
    {
        EmailFrom = "test@example.test", ResendApiKey = "local-test-key", TwilioAccountSid = "AC" + new string('a', 32),
        TwilioAuthToken = "local-test-key", TwilioVerifyServiceSid = "VA" + new string('b', 32)
    };

    [Theory]
    [InlineData("email", " PERSON@example.test ", "person@example.test")]
    [InlineData("phone", "+48123456789", "+48123456789")]
    public void ContactNormalization(string channel, string input, string expected)
    { Assert.True(OneTimeVerificationService.TryNormalize(channel, input, out var actual)); Assert.Equal(expected, actual); }

    [Theory]
    [InlineData("email", "User <person@example.test>")]
    [InlineData("email", "person@example.test\r\nBcc:other@example.test")]
    [InlineData("phone", "123456789")]
    [InlineData("phone", "+0123456789")]
    [InlineData("unknown", "person@example.test")]
    public void InvalidContactsAreRejected(string channel, string input)
        => Assert.False(OneTimeVerificationService.TryNormalize(channel, input, out _));

    [Fact]
    public async Task EmailProofIsBoundToPurposeAndAccountAndConsumedOnce()
    {
        var delivery = new Delivery(); var service = new OneTimeVerificationService(delivery, Enabled, new Clock());
        var challenge = await service.StartAsync(new("email", "person@example.test", "link"), "owner-a", default);
        Assert.Null(await service.VerifyAsync(challenge.VerificationId, delivery.Code, "register", null, default));
        Assert.Null(await service.VerifyAsync(challenge.VerificationId, delivery.Code, "link", "owner-b", default));
        var result = await service.VerifyAsync(challenge.VerificationId, delivery.Code, "link", "owner-a", default);
        Assert.Equal(new VerifiedIdentity("email", "person@example.test"), result);
        Assert.Null(await service.VerifyAsync(challenge.VerificationId, delivery.Code, "link", "owner-a", default));
    }

    [Fact]
    public async Task ProofHasOnlyOneWinnerUnderConcurrentRequests()
    {
        var delivery = new Delivery(); var service = new OneTimeVerificationService(delivery, Enabled, new Clock());
        var challenge = await service.StartAsync(new("email", "person@example.test", "login"), null, default);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.VerifyAsync(challenge.VerificationId, delivery.Code, "login", null, default)));
        Assert.Single(results.Where(result => result is not null));
    }

    [Fact]
    public async Task FiveBadCodesLockProofAndResendInvalidatesOldChallenge()
    {
        var clock = new Clock(); var delivery = new Delivery(); var service = new OneTimeVerificationService(delivery, Enabled, clock);
        var first = await service.StartAsync(new("email", "person@example.test", "login"), null, default);
        var firstCode = delivery.Code;
        var wrongCode = firstCode == "000000" ? "111111" : "000000";
        for (var attempt = 0; attempt < 5; attempt++) Assert.Null(await service.VerifyAsync(first.VerificationId, wrongCode, "login", null, default));
        Assert.Null(await service.VerifyAsync(first.VerificationId, firstCode, "login", null, default));
        await Assert.ThrowsAsync<VerificationRateLimitException>(() => service.StartAsync(new("email", "person@example.test", "login"), null, default));
        clock.Now = clock.Now.AddSeconds(60);
        var second = await service.StartAsync(new("email", "person@example.test", "login"), null, default);
        Assert.Null(await service.VerifyAsync(first.VerificationId, firstCode, "login", null, default));
        Assert.NotNull(await service.VerifyAsync(second.VerificationId, delivery.Code, "login", null, default));
    }

    [Fact]
    public async Task ExpiredProofAndProviderFailureAreSafe()
    {
        var clock = new Clock(); var delivery = new Delivery(); var service = new OneTimeVerificationService(delivery, Enabled, clock);
        var first = await service.StartAsync(new("email", "person@example.test", "login"), null, default);
        clock.Now = clock.Now.AddMinutes(10);
        Assert.Null(await service.VerifyAsync(first.VerificationId, delivery.Code, "login", null, default));
        delivery.Fail = true;
        var exception = await Assert.ThrowsAsync<VerificationDeliveryException>(() => service.StartAsync(new("email", "other@example.test", "login"), null, default));
        Assert.DoesNotContain("private", exception.Message);
    }

    [Fact]
    public async Task PhoneProofCallsProviderOnlyForBoundChallenge()
    {
        var delivery = new Delivery(); var service = new OneTimeVerificationService(delivery, Enabled, new Clock());
        var challenge = await service.StartAsync(new("phone", "+48123456789", "link"), "owner-a", default);
        Assert.Null(await service.VerifyAsync(challenge.VerificationId, "654321", "link", "owner-b", default));
        Assert.Equal(0, delivery.PhoneChecks);
        Assert.NotNull(await service.VerifyAsync(challenge.VerificationId, "654321", "link", "owner-a", default));
        Assert.Equal(1, delivery.PhoneChecks);
    }

    [Fact]
    public async Task DisabledChannelsNeverCallProviders()
    {
        var delivery = new Delivery(); var service = new OneTimeVerificationService(delivery, new(), new Clock());
        await Assert.ThrowsAsync<VerificationDeliveryException>(() => service.StartAsync(new("email", "person@example.test", "register"), null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync(new("email", "person@example.test", "link"), null, default));
        Assert.Equal(0, delivery.Sent);
    }

    [Fact]
    public void GoogleExchangeRequiresPkceAndIsSingleUse()
    {
        var clock = new Clock(); var tickets = new GoogleMobileTicketStore(clock);
        var verifier = new string('a', 64); var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var flow = new GoogleMobileFlow(challenge, new string('s', 32), null);
        Assert.True(GoogleMobileTicketStore.ValidFlow(challenge, flow.State));
        var code = tickets.Create("owner-a", flow);
        Assert.Null(tickets.Exchange(code, new string('b', 64)));
        Assert.Equal("owner-a", tickets.Exchange(code, verifier)?.UserId);
        Assert.Null(tickets.Exchange(code, verifier));
        var expired = tickets.Create("owner-a", flow); clock.Now = clock.Now.AddMinutes(2);
        Assert.Null(tickets.Exchange(expired, verifier));
    }

    [Fact]
    public void GoogleLinkAndExternalCallbackTicketsAreBoundAndSingleUse()
    {
        var clock = new Clock(); var tickets = new GoogleMobileTicketStore(clock);
        var flow = new GoogleMobileFlow(new string('a', 43), new string('s', 32), "owner-a");
        var link = tickets.StartLink(flow); Assert.Equal(flow, tickets.TakeLink(link)); Assert.Null(tickets.TakeLink(link));
        var callback = tickets.StartCallback(); Assert.True(tickets.TakeCallback(callback)); Assert.False(tickets.TakeCallback(callback));
        var expired = tickets.StartLink(flow); clock.Now = clock.Now.AddMinutes(2); Assert.Null(tickets.TakeLink(expired));
        Assert.False(GoogleMobileTicketStore.ValidFlow("https://evil.test", flow.State));
    }

    [Fact]
    public void PendingGoogleLinkRequiresBothPkceAndSameAuthenticatedAccount()
    {
        var tickets = new GoogleMobileTicketStore(new Clock()); var verifier = new string('a', 64);
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var code = tickets.CreatePendingLink("verified-google-subject", "person@example.test", new(challenge, new string('s', 32), "owner-a"));
        Assert.Null(tickets.Exchange(code, verifier)); Assert.Null(tickets.Exchange(code, verifier, "owner-b"));
        Assert.Null(tickets.Exchange(code, new string('b', 64), "owner-a"));
        var proof = tickets.Exchange(code, verifier, "owner-a"); Assert.NotNull(proof); Assert.Null(proof.UserId);
        Assert.Equal(new PendingGoogleLink("owner-a", "verified-google-subject", "person@example.test"), proof.Link);
        Assert.Null(tickets.Exchange(code, verifier, "owner-a"));
    }
}

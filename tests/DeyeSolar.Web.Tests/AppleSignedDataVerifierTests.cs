using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DeyeSolar.Web.Billing;

namespace DeyeSolar.Web.Tests;

public sealed class AppleSignedDataVerifierTests
{
    [Fact]
    public void TrustedSignedSubscriptionPreservesExpirationRevocationAndAccountToken()
    {
        using var fixture = new AppleSignedFixture();
        var revokedAt = fixture.Now.AddHours(-1);
        var payload = fixture.Transaction();
        payload["revocationDate"] = revokedAt.ToUnixTimeMilliseconds();
        var transaction = fixture.Verifier.VerifyTransaction(fixture.Sign(payload));
        Assert.Equal(fixture.Token, transaction.AppAccountToken);
        Assert.Equal(fixture.Now.AddMonths(1), transaction.ExpiresAt);
        Assert.Equal(revokedAt, transaction.RevokedAt);
        Assert.Equal("1001", transaction.OriginalTransactionId);
    }

    [Theory]
    [InlineData("bundleId", "foreign.app")]
    [InlineData("environment", "Production")]
    [InlineData("productId", "foreign.product")]
    [InlineData("type", "Non-Consumable")]
    [InlineData("appAccountToken", "invalid")]
    [InlineData("appAccountToken", "00000000-0000-0000-0000-000000000000")]
    [InlineData("transactionId", "1001/../../other")]
    [InlineData("offerDiscountType", "UNSUPPORTED")]
    public void WrongIdentityProductTypeOrTokenCannotVerify(string field, string value)
    {
        using var fixture = new AppleSignedFixture();
        var payload = fixture.Transaction();
        payload[field] = value;
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyTransaction(fixture.Sign(payload)));
    }

    [Theory]
    [InlineData("FREE_TRIAL", true)]
    [InlineData("PAY_AS_YOU_GO", false)]
    [InlineData("PAY_UP_FRONT", false)]
    [InlineData("ONE_TIME", false)]
    public void SignedOfferPaymentModeDistinguishesFreeTrialFromPaidOffer(string mode, bool isFreeTrial)
    {
        using var fixture = new AppleSignedFixture();
        var transaction = fixture.Transaction();
        transaction["offerDiscountType"] = mode;
        Assert.Equal(isFreeTrial, fixture.Verifier.VerifyTransaction(fixture.Sign(transaction)).IsFreeTrial);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4990, false)]
    public void SignedPriceWithoutOfferModeDistinguishesUnchargedFromPaidTransactions(long price, bool isFreeTrial)
    {
        using var fixture = new AppleSignedFixture();
        var transaction = fixture.Transaction();
        transaction["price"] = price;
        Assert.Equal(isFreeTrial, fixture.Verifier.VerifyTransaction(fixture.Sign(transaction)).IsFreeTrial);
    }

    [Fact]
    public void CurrentAppleProtocolAllowsTransactionWithoutOptionalPriceOrOfferMode()
    {
        using var fixture = new AppleSignedFixture();
        Assert.False(fixture.Verifier.VerifyTransaction(fixture.Sign(fixture.Transaction())).IsFreeTrial);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("\"0\"")]
    [InlineData("null")]
    [InlineData("{}")]
    public void InvalidSignedPriceReturnsTypedInvalidTransaction(string price)
    {
        using var fixture = new AppleSignedFixture();
        var transaction = fixture.Transaction();
        transaction["price"] = JsonSerializer.Deserialize<JsonElement>(price);
        var error = Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyTransaction(fixture.Sign(transaction)));
        Assert.Equal("invalid_apple_transaction", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public void MalformedPublicTransactionRenewalAndNotificationFieldsReturnTypedInvalidTransaction()
    {
        using var fixture = new AppleSignedFixture();
        foreach (var payload in new[] { "[]", "null", "42", "\"transaction\"" })
        {
            var signed = fixture.SignRaw(payload);
            Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyTransaction(signed));
            Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyRenewal(signed));
            Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyNotification(signed));
        }
        var renewal = fixture.Transaction();
        renewal["appAccountToken"] = 123;
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyRenewal(fixture.Sign(renewal)));
        var notification = fixture.Notification(fixture.Sign(fixture.Transaction()));
        ((Dictionary<string, object>)notification["data"])["signedTransactionInfo"] = 123;
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyNotification(fixture.Sign(notification)));
    }

    [Fact]
    public void MissingAccountBindingAndFutureSignedDateCannotVerify()
    {
        using var fixture = new AppleSignedFixture();
        var payload = fixture.Transaction();
        payload.Remove("appAccountToken");
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyTransaction(fixture.Sign(payload)));
        payload = fixture.Transaction();
        payload["signedDate"] = fixture.Now.AddMinutes(6).ToUnixTimeMilliseconds();
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyTransaction(fixture.Sign(payload)));
    }

    [Fact]
    public void TamperedPayloadAlgorithmSignatureAndUntrustedCertificateCannotVerify()
    {
        using var fixture = new AppleSignedFixture();
        var signed = fixture.Sign(fixture.Transaction());
        var segments = signed.Split('.');
        var altered = fixture.Transaction();
        altered["expiresDate"] = fixture.Now.AddYears(10).ToUnixTimeMilliseconds();
        segments[1] = AppleSignedDataVerifier.Encode(JsonSerializer.SerializeToUtf8Bytes(altered));
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyTransaction(string.Join('.', segments)));
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyTransaction(fixture.Sign(fixture.Transaction(), "HS256")));
        segments = signed.Split('.');
        segments[2] = AppleSignedDataVerifier.Encode(new byte[64]);
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyTransaction(string.Join('.', segments)));
        using var foreign = new AppleSignedFixture();
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyTransaction(foreign.Sign(foreign.Transaction())));
    }

    [Fact]
    public void AppleCertificateRoleMarkersAreRequired()
    {
        using var fixture = new AppleSignedFixture(includeAppleMarkers: false);
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyTransaction(fixture.Sign(fixture.Transaction())));
    }

    [Fact]
    public void NotificationChecksOuterAppIdentityAndNestedTransactionSignature()
    {
        using var fixture = new AppleSignedFixture();
        var notification = fixture.Notification(fixture.Sign(fixture.Transaction()));
        var verified = fixture.Verifier.VerifyNotification(fixture.Sign(notification));
        Assert.Equal("DID_RENEW", verified.Type);
        Assert.Equal(fixture.Token, verified.Transaction!.AppAccountToken);
        ((Dictionary<string, object>)notification["data"])["bundleId"] = "foreign.app";
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyNotification(fixture.Sign(notification)));
        notification = fixture.Notification("forged.transaction.signature");
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyNotification(fixture.Sign(notification)));
    }

    [Fact]
    public void SandboxAppIdMayBeAbsentButProductionAppIdMustMatch()
    {
        using var sandbox = new AppleSignedFixture();
        var notification = sandbox.Notification(sandbox.Sign(sandbox.Transaction()));
        ((Dictionary<string, object>)notification["data"]).Remove("appAppleId");
        Assert.NotNull(sandbox.Verifier.VerifyNotification(sandbox.Sign(notification)).Transaction);
        ((Dictionary<string, object>)notification["data"])["appAppleId"] = 0;
        Assert.NotNull(sandbox.Verifier.VerifyNotification(sandbox.Sign(notification)).Transaction);
        using var production = new AppleSignedFixture(environment: "Production");
        notification = production.Notification(production.Sign(production.Transaction()));
        ((Dictionary<string, object>)notification["data"])["appAppleId"] = 99;
        Assert.Throws<AppleBillingException>(() => production.Verifier.VerifyNotification(production.Sign(notification)));
        ((Dictionary<string, object>)notification["data"]).Remove("appAppleId");
        Assert.Throws<AppleBillingException>(() => production.Verifier.VerifyNotification(production.Sign(notification)));
    }

    [Fact]
    public void RevocationTransportFlagsAreDistinctFromExplicitRevocationOrBrokenTrust()
    {
        Assert.True(AppleSignedDataVerifier.IsRevocationUnavailable([new()
        { Status = X509ChainStatusFlags.OfflineRevocation | X509ChainStatusFlags.RevocationStatusUnknown }]));
        Assert.False(AppleSignedDataVerifier.IsRevocationUnavailable([new() { Status = X509ChainStatusFlags.Revoked }]));
        Assert.False(AppleSignedDataVerifier.IsRevocationUnavailable([new()
        { Status = X509ChainStatusFlags.OfflineRevocation | X509ChainStatusFlags.UntrustedRoot }]));
        Assert.False(AppleSignedDataVerifier.IsRevocationUnavailable([]));
    }

    [Fact]
    public void InvalidCredentialFilesCannotAdvertiseEnabledPurchasing()
    {
        using var fixture = new AppleSignedFixture();
        fixture.Options.Validate();
        File.WriteAllText(fixture.Options.PrivateKeyPath, "invalid private key");
        Assert.Throws<InvalidOperationException>(() => fixture.Options.Validate());
        File.WriteAllText(fixture.Options.PrivateKeyPath, fixture.Key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(fixture.Options.RootCertificatePaths[0], "invalid root");
        Assert.Throws<InvalidOperationException>(() => fixture.Options.Validate());
    }

    [Fact]
    public void DuplicateJsonPropertiesAndOversizedMalformedPayloadsCannotVerify()
    {
        using var fixture = new AppleSignedFixture();
        var json = JsonSerializer.Serialize(fixture.Transaction());
        json = json[..^1] + ",\"environment\":\"Sandbox\"}";
        Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyTransaction(fixture.SignRaw(json)));
        foreach (var invalid in new[] { "", "x.y.z", new string('x', 65537), "eyJhbGciOiJub25lIn0.e30." })
            Assert.Throws<AppleBillingException>(() => fixture.Verifier.VerifyTransaction(invalid));
    }

    [Fact]
    public async Task ApiClientUsesConfiguredAppleHostAndVerifiesBothSignedStatusObjects()
    {
        using var fixture = new AppleSignedFixture();
        var handler = new AppleHttpHandler(_ => fixture.StatusResponse(AppleSubscriptionStatus.Active));
        using var http = new HttpClient(handler);
        var client = new AppleAppStoreClient(http, fixture.Options, fixture.Verifier, fixture.Clock);
        var status = await client.ReadSubscriptionAsync("1001", default);
        Assert.Equal(AppleSubscriptionStatus.Active, status.Status);
        Assert.Equal(fixture.Token, status.Transaction.AppAccountToken);
        Assert.Equal("https://api.storekit-sandbox.apple.com/inApps/v1/subscriptions/1001", handler.Uri!.ToString());
        var jwt = handler.Authorization!.Split('.');
        using var payload = JsonDocument.Parse(AppleSignedDataVerifier.Decode(jwt[1]));
        Assert.Equal("appstoreconnect-v1", payload.RootElement.GetProperty("aud").GetString());
        Assert.Equal(fixture.Now.AddMinutes(5).ToUnixTimeSeconds(), payload.RootElement.GetProperty("exp").GetInt64());
        Assert.True(fixture.Key.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), AppleSignedDataVerifier.Decode(jwt[2]),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task SandboxStatusResponseMayOmitAppId()
    {
        using var fixture = new AppleSignedFixture();
        using var http = new HttpClient(new AppleHttpHandler(_ => fixture.StatusResponse(AppleSubscriptionStatus.Active)
            .Replace("\"appAppleId\":12345,", string.Empty, StringComparison.Ordinal)));
        var client = new AppleAppStoreClient(http, fixture.Options, fixture.Verifier, fixture.Clock);
        Assert.Equal(AppleSubscriptionStatus.Active, (await client.ReadSubscriptionAsync("1001", default)).Status);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("forged")]
    [InlineData("unknown-status")]
    public async Task ApiClientRejectsForeignEmptyForgedAndUnknownStatusResponses(string failure)
    {
        using var fixture = new AppleSignedFixture();
        var json = fixture.StatusResponse(AppleSubscriptionStatus.Active);
        if (failure == "foreign") json = json.Replace("com.dshapar.solar\"", "foreign.app\"", StringComparison.Ordinal);
        if (failure == "missing") json = JsonSerializer.Serialize(new { bundleId = fixture.Options.BundleId, environment = "Sandbox", appAppleId = 12345, data = Array.Empty<object>() });
        if (failure == "forged") json = json.Replace("signedTransactionInfo\":\"", "signedTransactionInfo\":\"forged", StringComparison.Ordinal);
        if (failure == "unknown-status") json = fixture.StatusResponse((AppleSubscriptionStatus)99);
        using var http = new HttpClient(new AppleHttpHandler(_ => json));
        var client = new AppleAppStoreClient(http, fixture.Options, fixture.Verifier, fixture.Clock);
        var exception = await Assert.ThrowsAsync<AppleStatusInvalidException>(() => client.ReadSubscriptionAsync("1001", default));
        Assert.False(exception.Retryable);
    }

    [Theory]
    [InlineData("\"status\":5,\"status\":1")]
    [InlineData("\"status\":1,\"status\":5")]
    public async Task ApiClientRejectsDuplicateAuthoritativeStatusesRegardlessOfOrder(string duplicate)
    {
        using var fixture = new AppleSignedFixture();
        var json = fixture.StatusResponse(AppleSubscriptionStatus.Active).Replace("\"status\":1", duplicate, StringComparison.Ordinal);
        using var http = new HttpClient(new AppleHttpHandler(_ => json));
        var client = new AppleAppStoreClient(http, fixture.Options, fixture.Verifier, fixture.Clock);
        var error = await Assert.ThrowsAsync<AppleStatusInvalidException>(() => client.ReadSubscriptionAsync("1001", default));
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task ApiTransportFailureIsRetryableAndDoesNotFallbackToAnotherEnvironment()
    {
        using var fixture = new AppleSignedFixture();
        var calls = 0;
        using var http = new HttpClient(new AppleHttpHandler(_ => { calls++; throw new HttpRequestException(); }));
        var client = new AppleAppStoreClient(http, fixture.Options, fixture.Verifier, fixture.Clock);
        var exception = await Assert.ThrowsAsync<AppleBillingException>(() => client.ReadSubscriptionAsync("1001", default));
        Assert.True(exception.Retryable);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ResponseBodyDeadlineIsRetryableEvenAfterHeadersHaveAlreadyArrived()
    {
        using var fixture = new AppleSignedFixture();
        var options = new AppleBillingOptions
        {
            Enabled = true, Environment = "Sandbox", PrivateKeyPath = fixture.Options.PrivateKeyPath,
            RequestTimeout = TimeSpan.FromMilliseconds(100)
        };
        using var http = new HttpClient(new StalledBodyHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new AppleAppStoreClient(http, options, fixture.Verifier, fixture.Clock);
        var task = client.ReadSubscriptionAsync("1001", default);
        var failure = await Assert.ThrowsAsync<AppleBillingException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(failure.Retryable);
        Assert.Equal("apple_unavailable", failure.Code);
    }

    [Fact]
    public async Task CallerCancellationDuringResponseBodyIsPropagated()
    {
        using var fixture = new AppleSignedFixture();
        using var http = new HttpClient(new StalledBodyHandler());
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var client = new AppleAppStoreClient(http, fixture.Options, fixture.Verifier, fixture.Clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadSubscriptionAsync("1001", caller.Token));
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) });
    }
    private sealed class StalledBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    private sealed class AppleHttpHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request)) });
        }
    }
}

internal sealed class AppleSignedFixture : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SolarAppleCertificates_" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 root;
    private readonly X509Certificate2 intermediate;
    private readonly X509Certificate2 leaf;
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public DateTimeOffset Now { get; } = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    public Guid Token { get; }
    public TimeProvider Clock { get; }
    public AppleBillingOptions Options { get; }
    public AppleSignedDataVerifier Verifier { get; }

    public AppleSignedFixture(bool includeAppleMarkers = true, string environment = "Sandbox", Guid? accountToken = null)
    {
        Token = accountToken ?? Guid.NewGuid();
        Directory.CreateDirectory(directory);
        var rootRequest = new CertificateRequest("CN=Synthetic Apple Test Root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        root = rootRequest.CreateSelfSigned(Now.AddDays(-2), Now.AddYears(2));
        var intermediateRequest = new CertificateRequest("CN=Synthetic Apple Test Intermediate", intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        if (includeAppleMarkers) intermediateRequest.CertificateExtensions.Add(new X509Extension("1.2.840.113635.100.6.2.1", [5, 0], false));
        using var unsignedIntermediate = intermediateRequest.Create(root, Now.AddDays(-1), Now.AddYears(1), [1, 2, 3, 4]);
        intermediate = unsignedIntermediate.CopyWithPrivateKey(intermediateKey);
        var leafRequest = new CertificateRequest("CN=Synthetic Apple Test Signing", Key, HashAlgorithmName.SHA256);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        if (includeAppleMarkers) leafRequest.CertificateExtensions.Add(new X509Extension("1.2.840.113635.100.6.11.1", [5, 0], false));
        leaf = leafRequest.Create(intermediate, Now.AddHours(-1), Now.AddMonths(6), [4, 3, 2, 1]);
        var rootPath = Path.Combine(directory, "root.cer");
        var keyPath = Path.Combine(directory, "key.p8");
        File.WriteAllBytes(rootPath, root.Export(X509ContentType.Cert));
        File.WriteAllText(keyPath, Key.ExportPkcs8PrivateKeyPem());
        Options = new AppleBillingOptions
        {
            Enabled = true,
            Environment = environment,
            AppAppleId = 12345,
            IssuerId = Guid.NewGuid().ToString(),
            KeyId = "ABCDEFGHIJ",
            PrivateKeyPath = keyPath,
            RootCertificatePaths = [rootPath]
        };
        Clock = new FixedClock(Now);
        Verifier = new AppleSignedDataVerifier(Options, Clock, X509RevocationMode.NoCheck);
    }

    public Dictionary<string, object> Transaction() => new()
    {
        ["transactionId"] = "1002",
        ["originalTransactionId"] = "1001",
        ["bundleId"] = Options.BundleId,
        ["productId"] = "com.dshapar.solar.monthly",
        ["type"] = "Auto-Renewable Subscription",
        ["appAccountToken"] = Token.ToString(),
        ["environment"] = Options.Environment,
        ["expiresDate"] = Now.AddMonths(1).ToUnixTimeMilliseconds(),
        ["signedDate"] = Now.ToUnixTimeMilliseconds()
    };

    public Dictionary<string, object> Notification(string transaction) => new()
    {
        ["notificationUUID"] = Guid.NewGuid().ToString(),
        ["notificationType"] = "DID_RENEW",
        ["version"] = "2.0",
        ["signedDate"] = Now.ToUnixTimeMilliseconds(),
        ["data"] = new Dictionary<string, object>
        {
            ["bundleId"] = Options.BundleId,
            ["environment"] = Options.Environment,
            ["appAppleId"] = 12345,
            ["signedTransactionInfo"] = transaction
        }
    };

    public string StatusResponse(AppleSubscriptionStatus status) => JsonSerializer.Serialize(new
    {
        bundleId = Options.BundleId,
        environment = Options.Environment,
        appAppleId = 12345,
        data = new[] { new { subscriptionGroupIdentifier = "test", lastTransactions = new[] { new
        {
            originalTransactionId = "1001", status = (int)status, signedTransactionInfo = Sign(Transaction()),
            signedRenewalInfo = Sign(new { originalTransactionId = "1001", productId = "com.dshapar.solar.monthly", environment = Options.Environment, signedDate = Now.ToUnixTimeMilliseconds() })
        } } } }
    });

    public string Sign(object value, string algorithm = "ES256") => SignRaw(JsonSerializer.Serialize(value), algorithm);
    public string SignRaw(string value, string algorithm = "ES256")
    {
        var header = AppleSignedDataVerifier.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            alg = algorithm,
            x5c = new[] { Convert.ToBase64String(leaf.RawData), Convert.ToBase64String(intermediate.RawData), Convert.ToBase64String(root.RawData) }
        }));
        var payload = AppleSignedDataVerifier.Encode(Encoding.UTF8.GetBytes(value));
        var signed = header + "." + payload;
        return signed + "." + AppleSignedDataVerifier.Encode(Key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public void Dispose()
    {
        Verifier.Dispose();
        leaf.Dispose(); intermediate.Dispose(); root.Dispose();
        Key.Dispose(); intermediateKey.Dispose(); rootKey.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}

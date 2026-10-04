using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeyeSolar.Web.Billing;

public sealed record AppleSubscriptionObservation(AppleTransaction Transaction, AppleSubscriptionStatus Status,
    DateTimeOffset? GracePeriodExpiresAt, DateTimeOffset SourceSignedAt, DateTimeOffset StartedAt, DateTimeOffset CheckedAt);

public interface IAppleAppStoreClient
{
    Task<AppleSubscriptionObservation> ReadSubscriptionAsync(string originalTransactionId, CancellationToken cancellationToken);
}

public sealed class AppleAppStoreClient(HttpClient http, AppleBillingOptions options,
    IAppleSignedDataVerifier verifier, TimeProvider clock) : IAppleAppStoreClient
{
    public async Task<AppleSubscriptionObservation> ReadSubscriptionAsync(string originalTransactionId, CancellationToken cancellationToken)
    {
        if (!options.Enabled) throw new AppleBillingException("Apple subscriptions are not configured.", "apple_unavailable", true);
        if (originalTransactionId.Length is < 1 or > 64 || !originalTransactionId.All(char.IsAsciiDigit))
            throw new AppleBillingException("Invalid Apple transaction identifier.", "invalid_apple_transaction");
        var startedAt = clock.GetUtcNow();
        var host = options.Environment == "Production" ? "https://api.storekit.apple.com" : "https://api.storekit-sandbox.apple.com";
        using var request = new HttpRequestMessage(HttpMethod.Get, host + "/inApps/v1/subscriptions/" + originalTransactionId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAuthorization());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                throw InvalidResponse(startedAt);
            if (!response.IsSuccessStatusCode)
                throw new AppleBillingException("Apple subscription status is temporarily unavailable.", "apple_unavailable", true);
            if (response.Content.Headers.ContentLength > 1024 * 1024) throw InvalidResponse(startedAt);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            // Cap the body even when Apple's HTTP response has no Content-Length.
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
            {
                if (buffer.Length + read > 1024 * 1024) throw InvalidResponse(startedAt);
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            AppleSignedDataVerifier.RejectDuplicateProperties(root);
            if (AppleSignedDataVerifier.Text(root, "bundleId", 200) != options.BundleId
                || AppleSignedDataVerifier.Text(root, "environment", 16) != options.Environment
                || options.Environment == "Production" && (!root.TryGetProperty("appAppleId", out var appId)
                    || !appId.TryGetInt64(out var actualId) || actualId != options.AppAppleId)) throw InvalidResponse(startedAt);
            AppleSubscriptionObservation? observation = null;
            foreach (var group in root.GetProperty("data").EnumerateArray())
                foreach (var item in group.GetProperty("lastTransactions").EnumerateArray())
                {
                    if (AppleSignedDataVerifier.Text(item, "originalTransactionId", 64) != originalTransactionId) continue;
                    if (observation is not null) throw InvalidResponse(startedAt);
                    var status = (AppleSubscriptionStatus)item.GetProperty("status").GetInt32();
                    if (!Enum.IsDefined(status)) throw InvalidResponse(startedAt);
                    var transaction = verifier.VerifyTransaction(AppleSignedDataVerifier.Text(item, "signedTransactionInfo", 65536));
                    var renewal = verifier.VerifyRenewal(AppleSignedDataVerifier.Text(item, "signedRenewalInfo", 65536));
                    if (transaction.OriginalTransactionId != originalTransactionId || renewal.OriginalTransactionId != originalTransactionId
                        || renewal.AppAccountToken is { } token && token != transaction.AppAccountToken) throw InvalidResponse(startedAt);
                    if (transaction.IsUpgraded && status == AppleSubscriptionStatus.Active) status = AppleSubscriptionStatus.Expired;
                    observation = new AppleSubscriptionObservation(transaction, status, renewal.GracePeriodExpiresAt,
                        transaction.SignedAt > renewal.SignedAt ? transaction.SignedAt : renewal.SignedAt, startedAt, clock.GetUtcNow());
                }
            // Missing authoritative data cannot retain a trusted paid grant.
            return observation ?? throw InvalidResponse(startedAt);
        }
        catch (AppleStatusInvalidException) { throw; }
        catch (AppleBillingException exception) when (!exception.Retryable) { throw InvalidResponse(startedAt); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or CryptographicException or ArgumentException)
        {
            throw InvalidResponse(startedAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new AppleBillingException("Apple subscription status is temporarily unavailable.", "apple_unavailable", true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppleBillingException("Apple subscription status is temporarily unavailable.", "apple_unavailable", true);
        }
    }

    private string CreateAuthorization()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(options.PrivateKeyPath));
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new AppleBillingException("Apple subscription credentials are unavailable.", "apple_unavailable", true);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var header = AppleSignedDataVerifier.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = options.KeyId, typ = "JWT" }));
        var payload = AppleSignedDataVerifier.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = options.IssuerId, iat = now, exp = now + 300, aud = "appstoreconnect-v1", bid = options.BundleId
        }));
        var signingInput = header + "." + payload;
        return signingInput + "." + AppleSignedDataVerifier.Encode(key.SignData(Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static AppleStatusInvalidException InvalidResponse(DateTimeOffset startedAt) => new(startedAt);
}

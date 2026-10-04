using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace DeyeSolar.Web.Billing;

public sealed record AppleTransaction(string TransactionId, string OriginalTransactionId, string ProductId,
    Guid AppAccountToken, string Environment, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt,
    DateTimeOffset SignedAt, bool IsUpgraded, bool IsFreeTrial = false);

public sealed record AppleRenewal(string OriginalTransactionId, string ProductId, Guid? AppAccountToken,
    DateTimeOffset? GracePeriodExpiresAt, DateTimeOffset SignedAt);

public sealed record AppleNotification(string NotificationId, string Type, AppleTransaction? Transaction);

public interface IAppleSignedDataVerifier
{
    AppleTransaction VerifyTransaction(string signedTransaction);
    AppleRenewal VerifyRenewal(string signedRenewal);
    AppleNotification VerifyNotification(string signedPayload);
}

public sealed class AppleSignedDataVerifier : IAppleSignedDataVerifier, IDisposable
{
    private readonly AppleBillingOptions options;
    private readonly TimeProvider clock;
    private readonly X509Certificate2[] roots;
    private readonly X509RevocationMode revocationMode;

    public AppleSignedDataVerifier(AppleBillingOptions options, TimeProvider clock)
        : this(options, clock, X509RevocationMode.Online) { }

    internal AppleSignedDataVerifier(AppleBillingOptions options, TimeProvider clock, X509RevocationMode revocationMode)
    {
        this.options = options;
        this.clock = clock;
        this.revocationMode = revocationMode;
        roots = options.Enabled ? options.RootCertificatePaths.Select(path => new X509Certificate2(path)).ToArray() : [];
    }

    public AppleTransaction VerifyTransaction(string signedTransaction)
    {
        using var document = Verify(signedTransaction);
        var data = document.RootElement;
        VerifyIdentity(data, requireBundle: true);
        var product = Text(data, "productId", 200);
        if (!options.ProductIds.Contains(product, StringComparer.Ordinal)
            || Text(data, "type", 64) != "Auto-Renewable Subscription"
            || !Guid.TryParse(Text(data, "appAccountToken", 36), out var accountToken) || accountToken == Guid.Empty)
            throw Invalid();
        var isFreeTrial = false;
        if (data.TryGetProperty("offerDiscountType", out var discount))
        {
            if (discount.ValueKind != JsonValueKind.String) throw Invalid();
            var mode = discount.GetString();
            if (mode is not ("FREE_TRIAL" or "PAY_AS_YOU_GO" or "PAY_UP_FRONT" or "ONE_TIME")) throw Invalid();
            isFreeTrial = mode == "FREE_TRIAL";
        }
        if (data.TryGetProperty("price", out var price))
        {
            if (price.ValueKind != JsonValueKind.Number || !price.TryGetInt64(out var amount) || amount < 0) throw Invalid();
            // Older signed transactions may omit offerDiscountType, but a zero-price purchase still cannot grant paid access.
            isFreeTrial |= amount == 0;
        }
        if (data.TryGetProperty("isUpgraded", out var upgradedFlag)
            && upgradedFlag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
        return new AppleTransaction(Identifier(data, "transactionId"), Identifier(data, "originalTransactionId"), product,
            accountToken, options.Environment, Date(data, "expiresDate"), OptionalDate(data, "revocationDate"),
            Date(data, "signedDate"), data.TryGetProperty("isUpgraded", out var upgraded) && upgraded.ValueKind == JsonValueKind.True, isFreeTrial);
    }

    public AppleRenewal VerifyRenewal(string signedRenewal)
    {
        using var document = Verify(signedRenewal);
        var data = document.RootElement;
        VerifyIdentity(data, requireBundle: false);
        var product = Text(data, "productId", 200);
        if (!options.ProductIds.Contains(product, StringComparer.Ordinal)) throw Invalid();
        Guid? accountToken = null;
        if (data.TryGetProperty("appAccountToken", out var token))
        {
            if (token.ValueKind != JsonValueKind.String || !Guid.TryParse(token.GetString(), out var parsed) || parsed == Guid.Empty) throw Invalid();
            accountToken = parsed;
        }
        return new AppleRenewal(Identifier(data, "originalTransactionId"), product, accountToken,
            OptionalDate(data, "gracePeriodExpiresDate"), Date(data, "signedDate"));
    }

    public AppleNotification VerifyNotification(string signedPayload)
    {
        using var document = Verify(signedPayload);
        var data = document.RootElement;
        if (Text(data, "version", 8) != "2.0") throw Invalid();
        var id = Text(data, "notificationUUID", 64);
        if (!Guid.TryParse(id, out _)) throw Invalid();
        var type = Text(data, "notificationType", 64);
        var identity = data.TryGetProperty("data", out var appData) ? appData
            : data.TryGetProperty("summary", out var summary) ? summary : throw Invalid();
        VerifyIdentity(identity, requireBundle: true);
        if (options.Environment == "Production" && (!identity.TryGetProperty("appAppleId", out var appId)
            || appId.ValueKind != JsonValueKind.Number || !appId.TryGetInt64(out var actualId) || actualId != options.AppAppleId)) throw Invalid();
        var transaction = identity.TryGetProperty("signedTransactionInfo", out _)
            ? VerifyTransaction(Text(identity, "signedTransactionInfo", 65536)) : null;
        return new AppleNotification(id, type, transaction);
    }

    private JsonDocument Verify(string signed)
    {
        if (!options.Enabled) throw new AppleBillingException("Apple subscriptions are not configured.", "apple_unavailable", true);
        if (string.IsNullOrWhiteSpace(signed) || signed.Length > 65536) throw Invalid();
        JsonDocument? payload = null;
        try
        {
            var segments = signed.Split('.');
            if (segments.Length != 3) throw Invalid();
            using var header = JsonDocument.Parse(Decode(segments[0]));
            payload = JsonDocument.Parse(Decode(segments[1]));
            RejectDuplicateProperties(header.RootElement);
            RejectDuplicateProperties(payload.RootElement);
            if (Text(header.RootElement, "alg", 16) != "ES256" || header.RootElement.TryGetProperty("crit", out _)) throw Invalid();
            var signedAt = Date(payload.RootElement, "signedDate");
            if (signedAt > clock.GetUtcNow().AddMinutes(5)) throw Invalid();
            var chainData = header.RootElement.GetProperty("x5c");
            if (chainData.ValueKind != JsonValueKind.Array || chainData.GetArrayLength() != 3) throw Invalid();
            using var leaf = new X509Certificate2(Convert.FromBase64String(chainData[0].GetString() ?? string.Empty));
            using var intermediate = new X509Certificate2(Convert.FromBase64String(chainData[1].GetString() ?? string.Empty));
            using var claimedRoot = new X509Certificate2(Convert.FromBase64String(chainData[2].GetString() ?? string.Empty));
            var signature = Decode(segments[2]);
            using var key = leaf.GetECDsaPublicKey();
            if (signature.Length != 64 || key is null || key.KeySize != 256
                || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
                || !key.VerifyData(Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]), signature,
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw Invalid();

            // Establish trusted Apple issuers before allowing online CRL/OCSP lookups.
            ValidateChain(leaf, intermediate, claimedRoot, X509RevocationMode.NoCheck, signedAt);
            if (revocationMode != X509RevocationMode.NoCheck)
                ValidateChain(leaf, intermediate, claimedRoot, revocationMode, clock.GetUtcNow());
            return payload;
        }
        catch (AppleBillingException) { payload?.Dispose(); throw; }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException
            or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
        {
            payload?.Dispose();
            throw Invalid();
        }
    }

    private void ValidateChain(X509Certificate2 leaf, X509Certificate2 intermediate, X509Certificate2 claimedRoot,
        X509RevocationMode mode, DateTimeOffset at)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.ExtraStore.Add(intermediate);
        chain.ChainPolicy.RevocationMode = mode;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
        chain.ChainPolicy.VerificationTime = at.UtcDateTime;
        if (!chain.Build(leaf))
        {
            if (mode != X509RevocationMode.NoCheck && IsRevocationUnavailable(chain.ChainStatus))
                throw new AppleBillingException("Apple certificate verification is temporarily unavailable.", "apple_unavailable", true);
            throw Invalid();
        }
        if (chain.ChainElements.Count != 3
            || !chain.ChainElements[1].Certificate.RawData.AsSpan().SequenceEqual(intermediate.RawData)
            || !chain.ChainElements[2].Certificate.RawData.AsSpan().SequenceEqual(claimedRoot.RawData)
            || !leaf.Extensions.Any(extension => extension.Oid?.Value == "1.2.840.113635.100.6.11.1")
            || !intermediate.Extensions.Any(extension => extension.Oid?.Value == "1.2.840.113635.100.6.2.1")) throw Invalid();
    }

    internal static bool IsRevocationUnavailable(X509ChainStatus[] statuses) => statuses.Length > 0 && statuses.All(status =>
        (status.Status & ~(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) == X509ChainStatusFlags.NoError);

    private void VerifyIdentity(JsonElement data, bool requireBundle)
    {
        if (Text(data, "environment", 16) != options.Environment
            || requireBundle && Text(data, "bundleId", 200) != options.BundleId) throw Invalid();
    }

    internal static string Text(JsonElement data, string name, int maxLength)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) throw Invalid();
        var value = property.GetString();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl)
            ? value : throw Invalid();
    }

    private static string Identifier(JsonElement data, string name)
    {
        var value = Text(data, name, 64);
        return value.All(char.IsAsciiDigit) ? value : throw Invalid();
    }

    private static DateTimeOffset Date(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var milliseconds)) throw Invalid();
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { throw Invalid(); }
    }
    private static DateTimeOffset? OptionalDate(JsonElement data, string name) => data.TryGetProperty(name, out var value)
        && value.ValueKind != JsonValueKind.Null ? Date(data, name) : null;

    internal static byte[] Decode(string value)
    {
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))) throw Invalid();
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
    }

    internal static string Encode(ReadOnlySpan<byte> value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static AppleBillingException Invalid() => new("The Apple subscription could not be verified for this app and account.", "invalid_apple_transaction");
    public void Dispose() { foreach (var root in roots) root.Dispose(); }
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DeyeSolar.Web.Billing;

public sealed class AppleBillingOptions
{
    public bool Enabled { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public string BundleId { get; init; } = "com.dshapar.solar";
    public string Environment { get; init; } = "Production";
    public long AppAppleId { get; init; }
    public string IssuerId { get; init; } = string.Empty;
    public string KeyId { get; init; } = string.Empty;
    public string PrivateKeyPath { get; init; } = string.Empty;
    public string[] RootCertificatePaths { get; init; } = [];
    public string[] ProductIds { get; init; } = ["com.dshapar.solar.monthly", "com.dshapar.solar.yearly"];
    public static TimeSpan MaximumStatusAge => BillingEntitlementPolicy.MaximumStatusAge;
    public BillingProductPolicy ProductPolicy => new(Enabled, Environment, ProductIds);
    public static TimeSpan RefreshInterval => TimeSpan.FromMinutes(15);

    // Capture before the user-editable SQL configuration provider is installed.
    public static AppleBillingOptions Capture(IConfiguration configuration)
    {
        var section = configuration.GetSection("Billing:Apple");
        var options = new AppleBillingOptions
        {
            Enabled = bool.TryParse(section["Enabled"], out var enabled) && enabled,
            RequestTimeout = TimeSpan.FromSeconds(int.TryParse(section["RequestTimeoutSeconds"], out var timeout) ? timeout : 30),
            BundleId = section["BundleId"] ?? "com.dshapar.solar",
            Environment = section["Environment"] ?? "Production",
            AppAppleId = long.TryParse(section["AppAppleId"], out var appId) ? appId : 0,
            IssuerId = section["IssuerId"] ?? string.Empty,
            KeyId = section["KeyId"] ?? string.Empty,
            PrivateKeyPath = section["PrivateKeyPath"] ?? string.Empty,
            RootCertificatePaths = section.GetSection("RootCertificatePaths").Get<string[]>() ?? [],
            ProductIds = section.GetSection("ProductIds").Get<string[]>()
                ?? ["com.dshapar.solar.monthly", "com.dshapar.solar.yearly"]
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("Billing:Apple:RequestTimeoutSeconds must be between 1 and 120.");
        if (!Enabled) return;
        if (Environment is not ("Sandbox" or "Production") || string.IsNullOrWhiteSpace(BundleId)
            || BundleId.Length > 200 || Environment == "Production" && AppAppleId <= 0 || !Guid.TryParse(IssuerId, out _)
            || KeyId.Length != 10 || !KeyId.All(char.IsAsciiLetterOrDigit)
            || !Path.IsPathFullyQualified(PrivateKeyPath) || !File.Exists(PrivateKeyPath)
            || RootCertificatePaths.Length == 0
            || RootCertificatePaths.Any(path => !Path.IsPathFullyQualified(path) || !File.Exists(path))
            || ProductIds.Length == 0 || ProductIds.Any(product => string.IsNullOrWhiteSpace(product) || product.Length > 200)
            || ProductIds.Distinct(StringComparer.Ordinal).Count() != ProductIds.Length)
            throw new InvalidOperationException("Billing:Apple requires valid operator credentials, app identity, product IDs and trusted Apple root certificate files.");
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(PrivateKeyPath));
            if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new CryptographicException();
            key.SignData([], HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            foreach (var path in RootCertificatePaths)
            {
                using var root = new X509Certificate2(path);
                if (!root.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority)
                    || !root.SubjectName.RawData.AsSpan().SequenceEqual(root.IssuerName.RawData))
                    throw new CryptographicException();
            }
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Billing:Apple requires a readable P-256 private signing key and valid trusted root certificates.");
        }
    }
}

public class AppleBillingException(string message, string code, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public int HttpStatus => Retryable ? 503 : Code == "apple_account_mismatch" ? 409 : 400;
}

public sealed class AppleStatusInvalidException(DateTimeOffset observationStartedAt)
    : AppleBillingException("Apple returned subscription information that could not be verified.", "invalid_apple_status")
{
    public DateTimeOffset ObservationStartedAt { get; } = observationStartedAt;
}

namespace DeyeSolar.Web.Auth;

// Captured from deployment configuration before editable installation settings are loaded.
public sealed class AuthAppleOptions
{
    public bool Enabled { get; init; }
    public string NativeClientId { get; init; } = "com.dshapar.solar";
    public string ServicesId { get; init; } = "";
    public string TeamId { get; init; } = "";
    public string KeyId { get; init; } = "";
    public string PrivateKeyPem { get; init; } = "";
    public string CallbackUrl { get; init; } = "https://solar.dshapar.com/auth/apple/callback";
    public bool NativeAvailable => Enabled && NativeClientId.Length > 0 && CredentialsAvailable;
    public bool WebAvailable => Enabled && ServicesId.Length > 0 && CredentialsAvailable
        && Uri.TryCreate(CallbackUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.AbsolutePath == "/auth/apple/callback" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    private bool CredentialsAvailable
    {
        get
        {
            if (TeamId.Length != 10 || KeyId.Length != 10 || !TeamId.All(char.IsAsciiLetterOrDigit) || !KeyId.All(char.IsAsciiLetterOrDigit)) return false;
            try
            {
                using var key = System.Security.Cryptography.ECDsa.Create(); key.ImportFromPem(PrivateKeyPem);
                if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") return false;
                _ = key.SignData([], System.Security.Cryptography.HashAlgorithmName.SHA256); return true;
            }
            catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or ArgumentException) { return false; }
        }
    }
    public static AuthAppleOptions Capture(IConfiguration config)
    {
        var privateKey = config["Auth:Apple:PrivateKeyPem"] ?? "";
        var file = config["Auth:Apple:PrivateKeyFile"];
        if (!string.IsNullOrEmpty(file)) privateKey = File.ReadAllText(file);
        return new() { Enabled = bool.TryParse(config["Auth:Apple:Enabled"], out var enabled) && enabled,
            NativeClientId = config["Auth:Apple:NativeClientId"] ?? "com.dshapar.solar", ServicesId = config["Auth:Apple:ServicesId"] ?? "",
            TeamId = config["Auth:Apple:TeamId"] ?? "", KeyId = config["Auth:Apple:KeyId"] ?? "", PrivateKeyPem = privateKey,
            CallbackUrl = config["Auth:Apple:CallbackUrl"] ?? "https://solar.dshapar.com/auth/apple/callback" };
    }
}

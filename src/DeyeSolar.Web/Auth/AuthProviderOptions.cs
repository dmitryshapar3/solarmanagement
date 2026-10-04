namespace DeyeSolar.Web.Auth;

public sealed class AuthProviderOptions
{
    public bool RegistrationEnabled { get; init; } = true;
    public string PublicBaseUrl { get; init; } = "https://solar.dshapar.com";
    public string GoogleClientId { get; init; } = "";
    public string GoogleClientSecret { get; init; } = "";
    public string ResendApiKey { get; init; } = "";
    public string EmailFrom { get; init; } = "";
    public string TwilioAccountSid { get; init; } = "";
    public string TwilioAuthToken { get; init; } = "";
    public string TwilioVerifyServiceSid { get; init; } = "";
    public bool EmailEnabled => !string.IsNullOrWhiteSpace(ResendApiKey) && !string.IsNullOrWhiteSpace(EmailFrom);
    public bool PhoneEnabled => !string.IsNullOrWhiteSpace(TwilioAccountSid) && !string.IsNullOrWhiteSpace(TwilioAuthToken)
        && System.Text.RegularExpressions.Regex.IsMatch(TwilioVerifyServiceSid, "^VA[0-9a-fA-F]{32}$");
    public bool GoogleEnabled => !string.IsNullOrWhiteSpace(GoogleClientId) && !string.IsNullOrWhiteSpace(GoogleClientSecret);

    public static AuthProviderOptions Capture(IConfiguration config) => new()
    {
        RegistrationEnabled = !bool.TryParse(config["Auth:RegistrationEnabled"], out var enabled) || enabled,
        PublicBaseUrl = config["Auth:PublicBaseUrl"] ?? "https://solar.dshapar.com",
        GoogleClientId = config["Auth:Google:ClientId"] ?? "",
        GoogleClientSecret = config["Auth:Google:ClientSecret"] ?? "",
        ResendApiKey = config["Auth:Email:ResendApiKey"] ?? "",
        EmailFrom = config["Auth:Email:From"] ?? "",
        TwilioAccountSid = config["Auth:Phone:AccountSid"] ?? "",
        TwilioAuthToken = config["Auth:Phone:AuthToken"] ?? "",
        TwilioVerifyServiceSid = config["Auth:Phone:VerifyServiceSid"] ?? ""
    };
}

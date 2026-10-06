using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Globalization;
using DeyeSolar.Web.Localization;

namespace DeyeSolar.Web.Auth;

public interface IIdentityVerificationDelivery
{
    Task SendEmailAsync(string destination, string code, CancellationToken ct);
    Task SendPhoneAsync(string destination, CancellationToken ct);
    Task<bool> CheckPhoneAsync(string destination, string code, CancellationToken ct);
}

public sealed class IdentityVerificationDelivery(IHttpClientFactory clients, AuthProviderOptions options, IHttpContextAccessor context) : IIdentityVerificationDelivery
{
    public const string ClientName = "IdentityVerification";
    public async Task SendEmailAsync(string destination, string code, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ResendApiKey);
        var text = context.HttpContext?.RequestServices.GetService<UiText>();
        const string message = "Your SmartSolar verification code is {0}. It expires in 10 minutes. If you did not request this code, ignore this email.";
        request.Content = JsonContent.Create(new { from = options.EmailFrom, to = new[] { destination },
            subject = text?["SmartSolar verification code"] ?? "SmartSolar verification code",
            text = text?.Format(message, code) ?? string.Format(CultureInfo.InvariantCulture, message, code) });
        using var response = await clients.CreateClient(ClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new VerificationDeliveryException();
    }

    public async Task SendPhoneAsync(string destination, CancellationToken ct)
    {
        var locale = UiText.Normalize(CultureInfo.CurrentUICulture.Name) ?? "en";
        using var request = PhoneRequest("Verifications", new() { ["To"] = destination, ["Channel"] = "sms",
            ["Locale"] = locale == "zh" ? "zh-CN" : locale });
        using var response = await clients.CreateClient(ClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new VerificationDeliveryException();
    }

    public async Task<bool> CheckPhoneAsync(string destination, string code, CancellationToken ct)
    {
        using var request = PhoneRequest("VerificationCheck", new() { ["To"] = destination, ["Code"] = code });
        using var response = await clients.CreateClient(ClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return false;
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return body.TryGetProperty("status", out var status) && status.GetString() == "approved";
    }

    private HttpRequestMessage PhoneRequest(string action, Dictionary<string, string> form)
    {
        // Service SID is not a host/path chosen by a client. Authentication stays in the header.
        if (!System.Text.RegularExpressions.Regex.IsMatch(options.TwilioVerifyServiceSid, "^VA[0-9a-fA-F]{32}$"))
            throw new VerificationDeliveryException();
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://verify.twilio.com/v2/Services/{options.TwilioVerifyServiceSid}/{action}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{options.TwilioAccountSid}:{options.TwilioAuthToken}")));
        request.Content = new FormUrlEncodedContent(form);
        return request;
    }
}

public sealed class VerificationDeliveryException : Exception
{
    public VerificationDeliveryException() : base("Verification delivery is temporarily unavailable.") { }
}

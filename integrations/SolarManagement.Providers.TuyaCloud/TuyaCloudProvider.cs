using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Providers.TuyaCloud;

public sealed class TuyaCloudProvider : SocketCloudProviderBase
{
    public const string ProviderId = "tuya.cloud";
    public static IReadOnlyList<string> Operations { get; } = ["test", "discover", "socket.inventory", "socket.read", "socket.set"];
    private static readonly string[] Origins = ["https://openapi.tuyaeu.com", "https://openapi.tuyaus.com", "https://openapi.tuyacn.com", "https://openapi.tuyain.com", "https://openapi-ueaz.tuyaus.com", "https://openapi-weaz.tuyaeu.com"];
    private readonly string _endpoint, _userId, _accessId, _accessSecret;
    private string? _token;
    private DateTimeOffset _tokenExpires;
    private readonly Dictionary<string, JsonElement> _specifications = new(StringComparer.Ordinal);
    public TuyaCloudProvider(WorkerConfiguration configuration, HttpClient? httpClient = null) : base(configuration, httpClient)
    {
        _endpoint = Endpoint(configuration, Origins[0], Origins, "/").TrimEnd('/');
        _userId = Identity(configuration.Configuration.Values, "userId");
        _accessId = Secret(configuration, "accessId");
        _accessSecret = Secret(configuration, "accessSecret");
    }
    private async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool tokenRequest = false)
    {
        if (!tokenRequest && (_token is null || DateTimeOffset.UtcNow >= _tokenExpires))
        {
            var token = await RequestAsync(HttpMethod.Get, "/v1.0/token?grant_type=1", null, ct, true);
            _token = Identity(token, "access_token");
            var ttl = Number(token, "expire_time");
            if (ttl is null or <= 30 or > 86400) throw new InvalidDataException("Tuya token expiry is invalid.");
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds((double)ttl.Value - 30);
        }
        var json = body is null ? "" : JsonSerializer.Serialize(body, IntegrationJson.Options);
        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        var stringToSign = method.Method + "\n" + contentHash + "\n\n" + path;
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_accessSecret), Encoding.UTF8.GetBytes(_accessId + (tokenRequest ? "" : _token) + t + stringToSign)));
        var request = new HttpRequestMessage(method, _endpoint + path);
        request.Headers.Add("client_id", _accessId);
        request.Headers.Add("t", t);
        request.Headers.Add("sign_method", "HMAC-SHA256");
        request.Headers.Add("sign", signature);
        if (!tokenRequest) request.Headers.Add("access_token", _token);
        if (body is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var result = await SendAsync(request, ct);
        if (Boolean(result, "success") != true || !result.TryGetProperty("result", out var value))
        { _token = null; throw new InvalidDataException("Tuya rejected the signed request or did not return its result."); }
        return value;
    }
    protected override async Task<IReadOnlyList<Entry>> InventoryAsync(CancellationToken ct)
    {
        var devices = new List<JsonElement>();
        // The endpoint is paged even though its response is a bare array without a total count.
        // A full last page requires a bounded look-ahead page to establish completeness.
        for (var page = 1; page <= 5; page++)
        {
            var result = await RequestAsync(HttpMethod.Get, $"/v1.0/users/{Escape(_userId)}/devices?page_no={page}&page_size=50", null, ct);
            if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > 50 || devices.Count + result.GetArrayLength() > 200)
                throw new InvalidDataException("Tuya inventory exceeds its bound or has invalid shape.");
            devices.AddRange(result.EnumerateArray());
            if (result.GetArrayLength() < 50) break;
            if (page == 5) throw new InvalidDataException("Tuya inventory is incomplete.");
        }
        if (devices.Select(d => Identity(d, "id")).Distinct(StringComparer.Ordinal).Count() != devices.Count)
            throw new InvalidDataException("Tuya returned duplicate device identities while paging.");
        var entries = new List<Entry>();
        foreach (var device in devices)
        {
            var id = Identity(device, "id");
            // Only documented socket and circuit breaker standard categories are admitted.
            var category = String(device, "category");
            if (category is not ("cz" or "pc" or "dlq")) continue;
            if (!_specifications.TryGetValue(id, out var spec))
            {
                spec = await RequestAsync(HttpMethod.Get, $"/v1.2/iot-03/devices/{Escape(id)}/specification", null, ct);
                if (spec.ValueKind != JsonValueKind.Object || String(spec, "category") != category) throw new InvalidDataException("Tuya device specification identity is inconsistent.");
                if (_specifications.Count >= 200) _specifications.Clear();
                _specifications[id] = spec;
            }
            var functionSet = Array(spec, "functions");
            var statusSet = Array(spec, "status");
            if (functionSet.GetArrayLength() > 256 || statusSet.GetArrayLength() > 256) throw new InvalidDataException("Tuya specification exceeds its bound.");
            var functions = functionSet.EnumerateArray().ToArray();
            var switches = functions.Where(f => String(f, "type") == "Boolean" && IsSwitchCode(String(f, "code"))).ToArray();
            if (switches.Length == 0) continue;
            var status = await RequestAsync(HttpMethod.Get, $"/v1.0/iot-03/devices/{Escape(id)}/status", null, ct);
            if (status.ValueKind != JsonValueKind.Array || status.GetArrayLength() > 256) throw new InvalidDataException("Tuya status is invalid.");
            var values = status.EnumerateArray().ToDictionary(s => Identity(s, "code"), s => s.GetProperty("value"), StringComparer.Ordinal);
            var powerSpec = statusSet.EnumerateArray().FirstOrDefault(s => String(s, "code") == "cur_power");
            decimal? power = null;
            var powerSupported = false;
            if (switches.Length == 1 && powerSpec.ValueKind == JsonValueKind.Object && String(powerSpec, "values") is { } powerValues)
            {
                using var dp = JsonDocument.Parse(powerValues);
                var unit = String(dp.RootElement, "unit");
                var scale = Number(dp.RootElement, "scale");
                if (unit == "W" && scale is >= 0 and <= 9 && scale == decimal.Truncate(scale.Value))
                {
                    powerSupported = true;
                    if (values.TryGetValue("cur_power", out var raw)) power = Decimal(raw) / (decimal)Math.Pow(10, (int)scale.Value);
                }
            }
            foreach (var s in switches)
            {
                if (entries.Count >= 200) throw new InvalidDataException("Tuya channel inventory exceeds its bound.");
                var channel = Identity(s, "code");
                bool? on = values.TryGetValue(channel, out var state) ? state.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
                entries.Add(new(new(id, channel, on, Boolean(device, "online"), Watts(power), null, DateTimeOffset.UtcNow),
                    (String(device, "name") ?? id) + (switches.Length > 1 ? " " + channel : ""), powerSupported,
                    IntegrationJson.Element(new { category, productId = String(device, "product_id"), switchCode = channel, circuit = "independent-channel", phases = (int?)null })));
            }
        }
        return entries;
    }
    private static bool IsSwitchCode(string? code) => code == "switch" || code is { } text && text.StartsWith("switch_", StringComparison.Ordinal)
        && int.TryParse(text[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 64;
    protected override async Task<string?> AccountIdentityAsync(CancellationToken ct)
    {
        var profile = await RequestAsync(HttpMethod.Get, $"/v1.0/users/{Escape(_userId)}/infos", null, ct);
        var uid = Identity(profile, "uid");
        if (uid != _userId) throw new InvalidDataException("Tuya returned a different user identity.");
        return "tuya-user:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid)));
    }
    protected override async Task SwitchAsync(Entry device, bool isOn, CancellationToken ct)
    {
        var result = await RequestAsync(HttpMethod.Post, $"/v1.0/iot-03/devices/{Escape(device.State.RemoteId)}/commands",
            new { commands = new[] { new { code = device.State.Channel, value = isOn } } }, ct);
        if (result.ValueKind != JsonValueKind.True) throw new InvalidDataException("Tuya did not accept the switch command.");
    }
}

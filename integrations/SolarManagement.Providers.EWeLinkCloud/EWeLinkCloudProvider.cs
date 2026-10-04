using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Providers.EWeLinkCloud;

public sealed class EWeLinkCloudProvider : SocketCloudProviderBase
{
    public const string ProviderId = "ewelink.cloud";
    public static IReadOnlyList<string> Operations { get; } = ["test", "discover", "socket.inventory", "socket.read", "socket.set"];
    private static readonly string[] Origins = ["https://eu-apia.coolkit.cc", "https://us-apia.coolkit.cc", "https://as-apia.coolkit.cc", "https://cn-apia.coolkit.cn"];
    private readonly string _endpoint, _appId, _accessToken;
    private DateTimeOffset _lastRequest;
    public EWeLinkCloudProvider(WorkerConfiguration configuration, HttpClient? httpClient = null) : base(configuration, httpClient)
    {
        _endpoint = Endpoint(configuration, Origins[0], Origins, "/").TrimEnd('/');
        _appId = Secret(configuration, "appId");
        _accessToken = Secret(configuration, "accessToken");
    }
    private async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var remaining = TimeSpan.FromSeconds(1) - (DateTimeOffset.UtcNow - _lastRequest);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
        var request = new HttpRequestMessage(method, _endpoint + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Add("X-CK-Appid", _appId);
        request.Headers.Add("X-CK-Nonce", Guid.NewGuid().ToString("N")[..8]);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, IntegrationJson.Options), Encoding.UTF8, "application/json");
        _lastRequest = DateTimeOffset.UtcNow;
        var response = await SendAsync(request, ct);
        if (Number(response, "error") != 0 || !response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("eWeLink did not accept the authorized request.");
        return data;
    }
    protected override async Task<IReadOnlyList<Entry>> InventoryAsync(CancellationToken ct)
    {
        var data = await RequestAsync(HttpMethod.Get, "/v2/device/thing?num=30", null, ct);
        var things = Array(data, "thingList");
        var total = Number(data, "total");
        // Vendor ordering cursor semantics and brand authorization can hide items. Do not present a partial authoritative inventory.
        if (total is null or < 0 or > 30 || total != things.GetArrayLength() || things.GetArrayLength() > 30)
            throw new InvalidDataException("eWeLink inventory is incomplete or exceeds the supported 30-item bound.");
        var entries = new List<Entry>();
        foreach (var thing in things.EnumerateArray())
        {
            if (Number(thing, "itemType") is not (1 or 2)) continue;
            var device = thing.GetProperty("itemData");
            var id = Identity(device, "deviceid");
            var uiid = device.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Object && extra.TryGetProperty("uiid", out var top) ? Decimal(top)
                : extra.ValueKind == JsonValueKind.Object && extra.TryGetProperty("extra", out var inner) ? Number(inner, "uiid") : null;
            if (!device.TryGetProperty("params", out var state) || state.ValueKind != JsonValueKind.Object) state = default;
            // UIIDs 138–141 are named in the public list but have no published parameter schema.
            var multi = uiid is 2 or 3 or 4 or 7 or 8 or 9;
            var single = uiid is 1 or 5 or 6 or 32;
            if (!single && !multi) continue;
            var channels = uiid is 2 or 7 ? 2 : uiid is 3 or 8 ? 3 : uiid is 4 or 9 ? 4 : 1;
            var on = SwitchState(String(state, "switch"));
            var canPower = uiid is 5 or 32;
            if (multi)
            {
                var switches = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("switches", out var arr) && arr.ValueKind == JsonValueKind.Array ? arr : default;
                var values = new Dictionary<int, bool?>();
                if (switches.ValueKind == JsonValueKind.Array)
                    foreach (var s in switches.EnumerateArray())
                    {
                        var outlet = Number(s, "outlet");
                        if (outlet is null || outlet < 0 || outlet >= channels || outlet != decimal.Truncate(outlet.Value) || !values.TryAdd((int)outlet, SwitchState(String(s, "switch"))))
                            throw new InvalidDataException("eWeLink channel identities are invalid.");
                    }
                for (var channel = 0; channel < channels; channel++)
                    entries.Add(new(new(id, channel.ToString(CultureInfo.InvariantCulture), values.GetValueOrDefault(channel), Boolean(device, "online"), null, null, DateTimeOffset.UtcNow),
                        (String(device, "name") ?? id) + " channel " + (channel + 1), false,
                        IntegrationJson.Element(new { uiid, multi = true, circuit = "independent-channel", phases = (int?)null })));
            }
            else entries.Add(new(new(id, "0", on, Boolean(device, "online"), canPower ? Watts(Number(state, "power")) : null, null, DateTimeOffset.UtcNow),
                String(device, "name") ?? id, canPower, IntegrationJson.Element(new { uiid, multi = false, phases = (int?)null })));
        }
        return entries;
    }
    private static bool? SwitchState(string? value) => value switch { "on" => true, "off" => false, _ => null };
    protected override async Task<string?> AccountIdentityAsync(CancellationToken ct)
    {
        var profile = await RequestAsync(HttpMethod.Get, "/v2/user/profile", null, ct);
        var userId = Identity(profile.GetProperty("user"), "apikey");
        return "ewelink-user:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)));
    }
    protected override async Task SwitchAsync(Entry device, bool isOn, CancellationToken ct)
    {
        object parameters = Boolean(device.Metadata, "multi") == true
            ? new { switches = new[] { new { outlet = int.Parse(device.State.Channel!, CultureInfo.InvariantCulture), @switch = isOn ? "on" : "off" } } }
            : new { @switch = isOn ? "on" : "off" };
        await RequestAsync(HttpMethod.Post, "/v2/device/thing/status", new { type = 1, id = device.State.RemoteId, @params = parameters }, ct);
    }
}

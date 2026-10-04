using SolarManagement.Integrations.WorkerSdk;
using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Providers.AqaraCloud;

public sealed class AqaraCloudProvider : SocketCloudProviderBase
{
    public const string ProviderId = "aqara.cloud";
    public static IReadOnlyList<string> Operations { get; } = ["test", "discover", "socket.inventory", "socket.read", "socket.set"];
    private static readonly string[] Origins = ["https://open-ger.aqara.com", "https://open-usa.aqara.com", "https://open-cn.aqara.com", "https://open-sg.aqara.com", "https://open-kr.aqara.com", "https://open-ru.aqara.com"];
    private readonly string _endpoint, _appId, _keyId, _appKey, _accessToken;
    private readonly Dictionary<string, JsonElement> _resources = new(StringComparer.Ordinal);
    public AqaraCloudProvider(WorkerConfiguration configuration, HttpClient? httpClient = null)
        : this(configuration, CloudProviderTransport.Create(configuration, httpClient)) { }
    public AqaraCloudProvider(WorkerConfiguration configuration, ICloudJsonTransport transport) : base(configuration, transport)
    {
        _endpoint = Endpoint(configuration, Origins[0] + "/v3.0/open/api", Origins, "/v3.0/open/api");
        _appId = Secret(configuration, "appId"); _keyId = Secret(configuration, "keyId");
        _appKey = Secret(configuration, "appKey"); _accessToken = Secret(configuration, "accessToken");
    }
    private async Task<JsonElement> RequestAsync(string intent, object data, CancellationToken ct)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var text = $"Accesstoken={_accessToken}&Appid={_appId}&Keyid={_keyId}&Nonce={nonce}&Time={time}{_appKey}";
        var sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant()))).ToLowerInvariant();
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new StringContent(JsonSerializer.Serialize(new { intent, data }, IntegrationJson.Options), Encoding.UTF8, "application/json") };
        request.Headers.Add("Accesstoken", _accessToken); request.Headers.Add("Appid", _appId); request.Headers.Add("Keyid", _keyId);
        request.Headers.Add("Nonce", nonce); request.Headers.Add("Time", time); request.Headers.Add("Sign", sign);
        var response = await SendAsync(request, ct);
        if (Number(response, "code") != 0 || !response.TryGetProperty("result", out var result)) throw new InvalidDataException("Aqara rejected the signed authorized request.");
        return result;
    }
    protected override async Task<IReadOnlyList<Entry>> InventoryAsync(CancellationToken ct)
    {
        var devices = new List<JsonElement>();
        int? expected = null;
        for (var page = 1; page <= 4; page++)
        {
            var result = await RequestAsync("query.device.info", new { pageNum = page, pageSize = 50 }, ct);
            var total = Number(result, "totalCount");
            if (total is null or < 0 or > 200 || total != decimal.Truncate(total.Value) || expected is { } count && count != total) throw new InvalidDataException("Aqara inventory count is invalid or changed during paging.");
            expected = (int)total;
            var data = Array(result, "data");
            if (data.GetArrayLength() > 50) throw new InvalidDataException("Aqara page exceeds its bound.");
            devices.AddRange(data.EnumerateArray());
            if (devices.Count == expected) break;
            if (devices.Count > expected || data.GetArrayLength() == 0 || page == 4) throw new InvalidDataException("Aqara inventory is incomplete.");
        }
        if (devices.Select(d => Identity(d, "did")).Distinct().Count() != devices.Count) throw new InvalidDataException("Aqara returned duplicate device identities.");
        var entries = new List<Entry>();
        foreach (var device in devices)
        {
            var model = Identity(device, "model");
            // The documented resource protocol represents sockets and wall switches; exclude locks, curtains, thermostats and hubs.
            if (!model.StartsWith("lumi.plug.", StringComparison.Ordinal) && !model.StartsWith("lumi.switch.", StringComparison.Ordinal) && !model.StartsWith("lumi.ctrl_", StringComparison.Ordinal)) continue;
            var id = Identity(device, "did");
            if (!_resources.TryGetValue(model, out var resources))
            {
                resources = await RequestAsync("query.resource.info", new { model }, ct);
                if (resources.ValueKind != JsonValueKind.Array || resources.GetArrayLength() > 256) throw new InvalidDataException("Aqara resource set is invalid.");
                var resourceIds = resources.EnumerateArray().Select(r => Identity(r, "resourceId")).ToArray();
                if (resourceIds.Distinct(StringComparer.Ordinal).Count() != resourceIds.Length || resources.EnumerateArray().Any(r => String(r, "model") is { } returnedModel && returnedModel != model))
                    throw new InvalidDataException("Aqara resource specification identities are inconsistent.");
                if (_resources.Count >= 200) _resources.Clear();
                _resources[model] = resources;
            }
            var switches = resources.EnumerateArray().Where(r => SwitchResource(String(r, "resourceId")) && Number(r, "access") is 5 or 7 && String(r, "enums") == "0,1").ToArray();
            if (switches.Length == 0) continue;
            var powerResource = resources.EnumerateArray().FirstOrDefault(r => String(r, "resourceId") == "0.12.85" && String(r, "unit") == "W" && Number(r, "access") is 1 or 3 or 5 or 7);
            // Numeric Aqara unit IDs have no public conversion table; do not infer watts from an unverified identifier.
            var canPower = switches.Length == 1 && powerResource.ValueKind == JsonValueKind.Object;
            var ids = switches.Select(r => Identity(r, "resourceId")).Concat(canPower ? ["0.12.85"] : System.Array.Empty<string>()).ToArray();
            var values = await RequestAsync("query.resource.value", new { resources = new[] { new { subjectId = id, resourceIds = ids } } }, ct);
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > ids.Length) throw new InvalidDataException("Aqara values are invalid.");
            var lookup = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var value in values.EnumerateArray())
            {
                var resourceId = Identity(value, "resourceId");
                if (String(value, "subjectId") != id || !ids.Contains(resourceId, StringComparer.Ordinal) || !lookup.TryAdd(resourceId, value)) throw new InvalidDataException("Aqara returned mismatched or duplicate resources.");
            }
            foreach (var s in switches)
            {
                if (entries.Count >= 200) throw new InvalidDataException("Aqara switch inventory exceeds its bound.");
                var channel = Identity(s, "resourceId");
                lookup.TryGetValue(channel, out var state);
                lookup.TryGetValue("0.12.85", out var power);
                var online = Number(device, "state") switch { 0 => (bool?)false, 1 => true, _ => null };
                var on = String(state, "value") switch { "0" => (bool?)false, "1" => true, _ => null };
                entries.Add(new(new(id, channel, on, online, canPower ? Watts(Number(power, "value")) : null,
                    Timestamp(Number(state, "timeStamp"), true), DateTimeOffset.UtcNow), (String(device, "deviceName") ?? id) + (switches.Length > 1 ? " " + channel : ""), canPower,
                    IntegrationJson.Element(new { model, resourceId = channel, circuit = "independent-channel", phases = (int?)null })));
            }
        }
        return entries;
    }
    private static bool SwitchResource(string? id) => id is { } value && value.StartsWith("4.", StringComparison.Ordinal) && value.EndsWith(".85", StringComparison.Ordinal)
        && int.TryParse(value[2..^3], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 16;
    protected override async Task SwitchAsync(Entry device, bool isOn, CancellationToken ct)
        => await RequestAsync("write.resource.device", new[] { new { subjectId = device.State.RemoteId, resources = new[] { new { resourceId = device.State.Channel, value = isOn ? "1" : "0" } } } }, ct);
}

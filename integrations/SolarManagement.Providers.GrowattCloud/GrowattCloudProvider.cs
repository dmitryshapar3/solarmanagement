using System.Globalization;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Providers.GrowattCloud;

/// <summary>Owner-authorized OSS API for the documented MIN/TLX device family (type 7).</summary>
public sealed class GrowattCloudProvider : CloudInverterProvider
{
    public const string ProviderId = "growatt.cloud";
    public static IReadOnlyList<string> Operations { get; } = ["test", "discover", "inverter.read", "inverter.history"];
    private List<string>? _plants;
    private DateTimeOffset _plantsExpire;
    private List<IntegrationDiscoveredDevice>? _inventory;
    private DateTimeOffset _inventoryExpire;
    private readonly Dictionary<string, (JsonElement Data, DateTimeOffset Expires)> _latest = new(StringComparer.Ordinal);
    private decimal PowerFactor => Value("powerUnit") switch { "W" => 1m, "kW" => 1000m, _ => 0m };

    public GrowattCloudProvider(WorkerConfiguration configuration) : this(configuration, null) { }
    public GrowattCloudProvider(WorkerConfiguration configuration, HttpClient? http)
        : base(configuration, http, "https://openapi.growatt.com")
    {
        _ = RequiredSecret("apiToken");
        if (Value("username") is not { Length: > 0 and <= 128 } username || username.Any(char.IsControl))
            throw new ArgumentException("An authorized ShineServer username is required.");
        if (Value("deviceType") is { } type && type != "7") throw new ArgumentException("Only documented MIN/TLX device type 7 is supported.");
    }

    private async Task<JsonElement> RequestAsync(string path, IReadOnlyDictionary<string, string> arguments, bool post, CancellationToken ct)
    {
        var request = post ? new HttpRequestMessage(HttpMethod.Post, BaseUrl + path) { Content = new FormUrlEncodedContent(arguments) }
            : new HttpRequestMessage(HttpMethod.Get, BaseUrl + path + "?" + string.Join("&", arguments.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        request.Headers.Add("token", RequiredSecret("apiToken"));
        var response = await SendAsync(request, ct);
        if (Number(response, "error_code") != 0 || !response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Growatt rejected the OSS API request.");
        return data.Clone();
    }

    private async Task<List<string>> PlantsAsync(CancellationToken ct)
    {
        if (_plants is not null && _plantsExpire > DateTimeOffset.UtcNow) return _plants;
        var plants = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var data = await RequestAsync("/v1/plant/user_plant_list", new Dictionary<string, string>
                { ["user_name"] = Value("username")!, ["page"] = page.ToString(CultureInfo.InvariantCulture), ["perpage"] = "100" }, true, ct);
            var items = data.GetProperty("plants");
            var count = Number(data, "count");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100 || count is null || count < 0 || count > 200)
                throw new InvalidDataException("Growatt plant inventory is invalid or exceeds its bound.");
            foreach (var item in items.EnumerateArray())
            {
                var plant = Text(item, "plant_id");
                if (string.IsNullOrWhiteSpace(plant) || plant.Length > 128 || plant.Any(char.IsControl) || plants.Contains(plant, StringComparer.Ordinal))
                    throw new InvalidDataException("Growatt plant identity is invalid.");
                plants.Add(plant);
            }
            if (plants.Count >= count)
            {
                if (plants.Count != count) throw new InvalidDataException("Growatt plant inventory count does not match.");
                _plants = plants;
                // The documented plant-list endpoint permits only ten requests/day.
                _plantsExpire = DateTimeOffset.UtcNow.AddHours(8);
                return plants;
            }
            if (items.GetArrayLength() == 0) throw new InvalidDataException("Growatt plant pagination is incomplete.");
        }
        throw new InvalidDataException("Growatt plant pagination exceeds its bound.");
    }

    private async Task<List<IntegrationDiscoveredDevice>> InventoryAsync(CancellationToken ct)
    {
        if (_inventory is not null && _inventoryExpire > DateTimeOffset.UtcNow) return _inventory;
        var devices = new List<IntegrationDiscoveredDevice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plant in await PlantsAsync(ct))
        {
            var received = 0;
            for (var page = 1; page <= 3; page++)
            {
                var data = await RequestAsync("/v1/device/list", new Dictionary<string, string>
                    { ["plant_id"] = plant, ["page"] = page.ToString(CultureInfo.InvariantCulture), ["perpage"] = "100" }, false, ct);
                var items = data.GetProperty("devices");
                var count = Number(data, "count");
                if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100 || count is null || count < 0 || count > 200)
                    throw new InvalidDataException("Growatt device inventory is invalid or exceeds its bound.");
                received += items.GetArrayLength();
                foreach (var item in items.EnumerateArray())
                {
                    if (Number(item, "type") != 7) continue;
                    var remoteId = Text(item, "device_sn");
                    if (string.IsNullOrWhiteSpace(remoteId) || remoteId.Length > 128 || remoteId != remoteId.Trim() || remoteId.Any(char.IsControl) || !seen.Add(remoteId) || devices.Count >= 200)
                        throw new InvalidDataException("Growatt device identity is invalid or duplicated.");
                    var latest = await LatestAsync(remoteId, ct);
                    devices.Add(new(remoteId, null, "inverter", Text(item, "model") is { Length: > 0 } model ? model + " " + remoteId : remoteId,
                        Value("username"), IntegrationJson.Element(new { capabilities = new { hasBattery = HasBattery(latest),
                            hasSolarPower = true, hasSignedGridPower = PowerFactor != 0, hasLoadPower = true,
                            hasGridPowerHistory = false, solarBasis = "PvDc" } })));
                }
                if (received >= count)
                {
                    if (received != count) throw new InvalidDataException("Growatt device inventory count does not match.");
                    break;
                }
                if (items.GetArrayLength() == 0 || page == 3) throw new InvalidDataException("Growatt device pagination is incomplete.");
            }
        }
        _inventory = devices;
        _inventoryExpire = DateTimeOffset.UtcNow.AddMinutes(5);
        return devices;
    }

    private async Task<JsonElement> LatestAsync(string remoteId, CancellationToken ct)
    {
        if (_latest.TryGetValue(remoteId, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Data;
        var data = await RequestAsync("/v1/device/tlx/tlx_last_data", new Dictionary<string, string> { ["tlx_sn"] = remoteId }, true, ct);
        if (Text(data, "serialNum") != remoteId) throw new InvalidDataException("Growatt returned another inverter's measurements.");
        _latest[remoteId] = (data, DateTimeOffset.UtcNow.AddMinutes(5));
        return data;
    }

    private static bool HasBattery(JsonElement data) => Number(data, "batteryNo") > 0
        || !string.IsNullOrWhiteSpace(Text(data, "batterySN")) || !string.IsNullOrWhiteSpace(Text(data, "batterySn"))
        || !string.IsNullOrWhiteSpace(Text(data, "batSn"));

    private DateTimeOffset? ObservedAt(JsonElement data)
    {
        if (data.TryGetProperty("calendar", out var calendar) && calendar.ValueKind == JsonValueKind.Object)
        {
            if (EpochMilliseconds(calendar, "timeInMillis") is { } epoch) return epoch;
            if (calendar.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object
                && EpochMilliseconds(time, "time") is { } nested) return nested;
        }
        return LocalTime(Text(data, "time"), "yyyy-MM-dd HH:mm:ss");
    }

    private ProviderMeasurement Watts(JsonElement data, string key, DateTimeOffset? observed)
        => PowerFactor == 0 ? data.TryGetProperty(key, out var field) && field.ValueKind != JsonValueKind.Null ? Invalid(observed) : Missing(observed)
            : Measure(data, key, observed, 0, int.MaxValue, PowerFactor);

    private ProviderInverterTelemetry Normalize(string remoteId, JsonElement data)
    {
        var observed = ObservedAt(data);
        var battery = HasBattery(data);
        var batteryPower = Missing(observed);
        if (battery)
        {
            var first = Difference(Watts(data, "bdc1DischargePower", observed), Watts(data, "bdc1ChargePower", observed));
            // A second BDC contributes only when both of its power fields are reported.
            if (data.TryGetProperty("bdc2DischargePower", out _) || data.TryGetProperty("bdc2ChargePower", out _))
            {
                var second = Difference(Watts(data, "bdc2DischargePower", observed), Watts(data, "bdc2ChargePower", observed));
                batteryPower = first.Quality == ProviderMeasurementQuality.Good && second.Quality == ProviderMeasurementQuality.Good
                    && first.Value + second.Value is >= int.MinValue and <= int.MaxValue
                    ? new(first.Value + second.Value, observed, ProviderMeasurementQuality.Good)
                    : first.Quality == ProviderMeasurementQuality.Missing || second.Quality == ProviderMeasurementQuality.Missing ? Missing(observed) : Invalid(observed);
            }
            else batteryPower = first;
        }
        return new(remoteId, DateTimeOffset.UtcNow, "PvDc", battery ? Measure(data, "bmsSoc", observed, 0, 100) : Missing(observed),
            batteryPower, Missing(observed), Missing(observed), Missing(observed), Watts(data, "ppv", observed),
            Difference(Watts(data, "pacToUserTotal", observed), Watts(data, "pacToGridTotal", observed)), Watts(data, "pacToLocalLoad", observed));
    }

    public override async Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        if (method is "test" or "discover")
        {
            var inventory = await InventoryAsync(ct);
            return method == "test" ? IntegrationJson.Element(new IntegrationTestResult(true, "connected", "Growatt OSS API connection verified.", Value("username")))
                : IntegrationJson.Element(inventory);
        }
        var remoteId = Identity(parameters);
        if (!(await InventoryAsync(ct)).Any(device => device.RemoteId == remoteId)) throw new InvalidOperationException("This inverter is not an authorized supported MIN/TLX device.");
        if (method == "inverter.read") return IntegrationJson.Element(Normalize(remoteId, await LatestAsync(remoteId, ct)));
        if (method == "inverter.history") throw new NotSupportedException("The available Growatt history specification lacks a verifiable response contract.");
        throw new InvalidOperationException("Unsupported Growatt operation.");
    }
}

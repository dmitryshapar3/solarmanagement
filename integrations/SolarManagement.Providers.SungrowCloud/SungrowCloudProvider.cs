using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Providers.SungrowCloud;

/// <summary>Official iSolarCloud owner-authorized, plaintext monitoring APIs.</summary>
public sealed class SungrowCloudProvider : CloudInverterProvider
{
    public const string ProviderId = "sungrow.cloud";
    public static IReadOnlyList<string> Operations { get; } = ["test", "discover", "inverter.read", "inverter.history"];
    public override int MinimumOperationTimeoutSeconds => 180;
    private static readonly string[] Points = ["13003", "13141", "13150", "13126", "13138", "13139", "13143", "13149", "13121", "13119"];
    private readonly Dictionary<string, JsonElement> _devices = new(StringComparer.Ordinal);
    public SungrowCloudProvider(WorkerConfiguration configuration) : this(configuration, null) { }
    public SungrowCloudProvider(WorkerConfiguration configuration, HttpClient? http)
        : base(configuration, http, "https://gateway.isolarcloud.eu") { }

    private async Task<JsonElement> PostAsync(string operation, Dictionary<string, object?> body, CancellationToken ct)
    {
        body["appkey"] = Value("appKey") ?? throw new ArgumentException("Sungrow app key is required.");
        body["lang"] = "_en_US";
        var request = JsonPost("/openapi/platform/" + operation, body);
        request.Headers.Add("x-access-key", RequiredSecret("accessKey"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequiredSecret("accessToken"));
        var result = await SendAsync(request, ct);
        if (Text(result, "result_code") != "1" || !result.TryGetProperty("result_data", out var data))
            throw new InvalidDataException("Sungrow rejected the data request.");
        return data.Clone();
    }
    private async Task<List<JsonElement>> PagesAsync(string operation, Dictionary<string, object?> body, CancellationToken ct)
    {
        var rows = new List<JsonElement>();
        for (var page = 1; page <= 3; page++)
        {
            body["page"] = page; body["size"] = 100;
            var data = await PostAsync(operation, body, ct);
            var count = Number(data, "row_count");
            var list = data.GetProperty("pageList");
            if (count is null || count < 0 || count > 200 || count != decimal.Truncate(count.Value)
                || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 100)
                throw new InvalidDataException("Sungrow inventory pagination is invalid.");
            rows.AddRange(list.EnumerateArray().Select(x => x.Clone()));
            if (rows.Count > count) throw new InvalidDataException("Sungrow inventory count is inconsistent.");
            if (rows.Count == count) return rows;
            if (list.GetArrayLength() == 0) throw new InvalidDataException("Sungrow inventory pagination did not advance.");
        }
        throw new InvalidDataException("Sungrow inventory exceeds the discovery bound.");
    }
    private async Task<List<IntegrationDiscoveredDevice>> DiscoverAsync(string? parent, CancellationToken ct)
    {
        var plants = await PagesAsync("queryPowerStationList", new() { ["ps_type"] = "4,5", ["valid_flag"] = "1,3" }, ct);
        var devices = new List<IntegrationDiscoveredDevice>();
        var seenPlants = new HashSet<string>(StringComparer.Ordinal);
        var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var plant in plants)
        {
            var plantId = Text(plant, "ps_id");
            if (string.IsNullOrWhiteSpace(plantId) || !seenPlants.Add(plantId)) throw new InvalidDataException("Sungrow plant identity is invalid.");
            if (parent is not null && parent != plantId) continue;
            foreach (var device in await PagesAsync("getDeviceListByPsId", new() { ["ps_id"] = plantId,
                         ["is_virtual_unit"] = "0", ["device_type_list"] = new[] { 14 } }, ct))
            {
                if (Number(device, "device_type") != 14) continue;
                var key = Text(device, "ps_key");
                if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl) || found.Count >= 200
                    || !found.TryAdd(key, device)) throw new InvalidDataException("Sungrow device identity is invalid.");
                devices.Add(new(key, null, "inverter", Text(device, "device_name") ?? key, null,
                    IntegrationJson.Element(new { stationId = plantId, serialNumber = Text(device, "device_sn"), model = Text(device, "device_model_code"),
                        capabilities = new { hasBattery = true, hasSolarPower = true, hasSignedGridPower = true,
                            hasLoadPower = true, hasGridPowerHistory = !string.IsNullOrWhiteSpace(Value("deviceTimeZone")), solarBasis = "PvDc" } })));
            }
        }
        // A filtered discovery must not erase identities selected from other plants.
        if (parent is null) _devices.Clear();
        foreach (var item in found) _devices[item.Key] = item.Value;
        return devices;
    }
    private async Task<JsonElement> DeviceAsync(string remoteId, CancellationToken ct)
    {
        if (!_devices.TryGetValue(remoteId, out var device))
        {
            await DiscoverAsync(null, ct);
            if (!_devices.TryGetValue(remoteId, out device)) throw new InvalidDataException("Sungrow device is outside the authorized inventory.");
        }
        return device;
    }
    private static ProviderMeasurement Point(JsonElement data, string point, DateTimeOffset? time, decimal min = 0, decimal max = int.MaxValue)
        => Measure(data, "p" + point, time, min, max);
    private static ProviderMeasurement Grid(JsonElement data, DateTimeOffset? time)
        => Difference(Point(data, "13149", time), Point(data, "13121", time));
    private static void ValidateUnits(JsonElement data)
    {
        if (!data.TryGetProperty("point_dict", out var dictionary)) return;
        if (dictionary.ValueKind != JsonValueKind.Array || dictionary.GetArrayLength() > 1000)
            throw new InvalidDataException("Sungrow measuring point dictionary is invalid.");
        foreach (var entry in dictionary.EnumerateArray())
        {
            var id = Text(entry, "point_id");
            var expected = id switch { "13003" or "13149" or "13121" or "13150" or "13126" or "13119" => "W",
                "13138" => "V", "13139" => "A", _ => null };
            if (expected is not null && Text(entry, "point_unit") != expected)
                throw new InvalidDataException("Sungrow measuring point unit contradicts its API contract.");
        }
    }
    private async Task<ProviderInverterTelemetry> ReadAsync(string remoteId, CancellationToken ct)
    {
        var device = await DeviceAsync(remoteId, ct);
        var data = await PostAsync("getDeviceRealTimeData", new() { ["ps_key_list"] = new[] { remoteId },
            ["device_type"] = 14, ["point_id_list"] = Points, ["is_get_point_dict"] = "1" }, ct);
        ValidateUnits(data);
        var list = data.GetProperty("device_point_list");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != 1) throw new InvalidDataException("Sungrow did not return one selected device.");
        var point = list[0].GetProperty("device_point");
        if (Text(point, "ps_key") != remoteId || Text(point, "device_sn") != Text(device, "device_sn"))
            throw new InvalidDataException("Sungrow returned a different device.");
        var time = LocalTime(Text(point, "device_time"), "yyyyMMddHHmmss");
        // The reference specifies minimum units (W, V, A); blank SOC unit is a battery percentage.
        return new(remoteId, DateTimeOffset.UtcNow, "PvDc", Point(point, "13141", time, 0, 100),
            Difference(Point(point, "13150", time), Point(point, "13126", time)), Point(point, "13143", time, -100, 200),
            Point(point, "13138", time, 0, 2000), Point(point, "13139", time, -10000, 10000),
            Point(point, "13003", time), Grid(point, time), Point(point, "13119", time));
    }
    private async Task<ProviderGridHistory> HistoryAsync(string remoteId, JsonElement parameters, CancellationToken ct)
    {
        var (start, end) = HistoryInterval(parameters);
        await DeviceAsync(remoteId, ct);
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(Value("deviceTimeZone") ?? ""); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new ArgumentException("The plant time zone is required for Sungrow history."); }
        var localStart = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        var localEnd = TimeZoneInfo.ConvertTime(end, zone).DateTime;
        // Vendor endpoints cannot disambiguate a DST fold, or serve today's history.
        if (localEnd <= localStart || zone.IsAmbiguousTime(localStart) || zone.IsAmbiguousTime(localEnd))
            throw new ArgumentException("The requested local history interval is ambiguous.");
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
        var complete = localEnd <= today && zone.GetUtcOffset(start) == zone.GetUtcOffset(end);
        var effectiveEnd = localEnd < today ? localEnd : today;
        var samples = new SortedDictionary<DateTimeOffset, int>();
        for (var cursor = localStart; cursor < effectiveEnd; cursor = cursor.AddHours(3))
        {
            var stop = cursor.AddHours(3) < effectiveEnd ? cursor.AddHours(3) : effectiveEnd;
            var data = await PostAsync("getDevicePointMinuteDataList", new() { ["ps_key_list"] = new[] { remoteId },
                ["points"] = "p13149,p13121", ["minute_interval"] = "5", ["is_get_point_dict"] = "1",
                ["start_time_stamp"] = cursor.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                ["end_time_stamp"] = stop.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) }, ct);
            ValidateUnits(data);
            if (!data.TryGetProperty(remoteId, out var points) || points.ValueKind != JsonValueKind.Array || points.GetArrayLength() > 1000)
            { complete = false; continue; }
            if (points.GetArrayLength() == 0) complete = false;
            foreach (var point in points.EnumerateArray())
            {
                var time = LocalTime(Text(point, "time_stamp"), "yyyyMMddHHmmss");
                var grid = Grid(point, time);
                if (time is null || grid.Quality != ProviderMeasurementQuality.Good || grid.Value != decimal.Truncate(grid.Value ?? 0))
                { complete = false; continue; }
                if (time >= start && time < end)
                {
                    if (samples.TryGetValue(time.Value, out var previous) && previous != (int)grid.Value!.Value) complete = false;
                    else samples[time.Value] = (int)grid.Value!.Value;
                }
            }
        }
        return new(remoteId, samples.Select(x => new ProviderGridSample(x.Key, x.Value)).ToArray(), null, complete);
    }
    public override async Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Value("appKey")) || string.IsNullOrWhiteSpace(Secret("accessKey")) || string.IsNullOrWhiteSpace(Secret("accessToken")))
        {
            if (method == "test") return Connection(false, "authorization_required", "Supply the approved app key, access key and owner's OAuth access token.");
            throw new InvalidOperationException("Sungrow authorization is required.");
        }
        if (method == "test") { await DiscoverAsync(null, ct); return Connection(true, "connected", "Sungrow authorized inventory verified."); }
        if (method == "discover") return IntegrationJson.Element(await DiscoverAsync(Text(parameters, "parentId"), ct));
        var remoteId = Identity(parameters);
        if (method == "inverter.read") return IntegrationJson.Element(await ReadAsync(remoteId, ct));
        if (method == "inverter.history") return IntegrationJson.Element(await HistoryAsync(remoteId, parameters, ct));
        throw new NotSupportedException("Unsupported Sungrow operation.");
    }
}

using System.Net.Http.Headers;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Providers.HuaweiFusionSolar;

/// <summary>SmartPVMS 25.2 northbound monitoring APIs; no portal scraping or device controls.</summary>
public sealed class HuaweiFusionSolarProvider : CloudInverterProvider
{
    public const string ProviderId = "huawei.fusionsolar";
    public static IReadOnlyList<string> Operations { get; } = ["test", "discover", "inverter.read", "inverter.history"];
    public override int MinimumOperationTimeoutSeconds => 120;
    private readonly Dictionary<string, JsonElement> _devices = new(StringComparer.Ordinal);
    private string? _xsrf;
    private DateTimeOffset _loginAt;
    private bool _loginRejected;
    public HuaweiFusionSolarProvider(WorkerConfiguration configuration) : this(configuration, null) { }
    public HuaweiFusionSolarProvider(WorkerConfiguration configuration, HttpClient? http)
        : base(configuration, http, "https://eu5.fusionsolar.huawei.com") { }
    private bool OAuth => Value("authMode") == "accessToken";
    private decimal GridSign => Sign("gridPositiveDirection", "import", "export");
    private decimal BatterySign => Sign("batteryPositiveDirection", "discharge", "charge");
    private static ProviderMeasurement Signed(JsonElement data, string key, decimal sign)
    {
        var measurement = Measure(data, key, null, int.MinValue, int.MaxValue, sign == 0 ? 1 : sign);
        return sign == 0 && measurement.Quality != ProviderMeasurementQuality.Missing ? Invalid() : measurement;
    }
    private static JsonElement Data(JsonElement result)
    {
        if (!result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True
            || Number(result, "failCode") != 0 || !result.TryGetProperty("data", out var data))
            throw new InvalidDataException("FusionSolar rejected the data request.");
        return data.Clone();
    }
    private async Task AuthenticateAsync(CancellationToken ct)
    {
        if (OAuth || _xsrf is not null && DateTimeOffset.UtcNow - _loginAt < TimeSpan.FromMinutes(29)) return;
        if (_loginRejected) throw new InvalidOperationException("FusionSolar login was rejected. Update the integration credentials before retrying.");
        var username = Value("username");
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || username.Any(char.IsControl))
            throw new ArgumentException("The northbound API account is required.");
        string? token = null;
        var result = await SendAsync(JsonPost("/thirdData/login", new { userName = username, systemCode = RequiredSecret("systemCode") }), ct,
            response => { if (response.Headers.TryGetValues("XSRF-TOKEN", out var values)) token = values.SingleOrDefault(); });
        try
        {
            Data(result);
            if (string.IsNullOrWhiteSpace(token) || token.Length > 8192 || token.Any(char.IsControl))
                throw new InvalidDataException("FusionSolar login did not return an authentication token.");
            _xsrf = token; _loginAt = DateTimeOffset.UtcNow;
        }
        catch { _loginRejected = true; throw; }
    }
    private async Task<JsonElement> PostAsync(string operation, object body, CancellationToken ct)
    {
        await AuthenticateAsync(ct);
        var request = JsonPost("/thirdData/" + operation, body);
        if (OAuth) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequiredSecret("accessToken"));
        else request.Headers.Add("XSRF-TOKEN", _xsrf!);
        var result = await SendAsync(request, ct);
        // An expired credential is reported without replaying potentially rate-limited logins.
        if (!OAuth && Number(result, "failCode") == 305) _xsrf = null;
        return Data(result);
    }
    private async Task<List<IntegrationDiscoveredDevice>> DiscoverAsync(string? parent, CancellationToken ct)
    {
        var stations = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= 3; page++)
        {
            var data = await PostAsync("stations", new { pageNo = page }, ct);
            var pageCount = Number(data, "pageCount"); var total = Number(data, "total");
            var list = data.GetProperty("list");
            if (Number(data, "pageNo") != page || pageCount is null || pageCount < 0 || pageCount > 2
                || pageCount != decimal.Truncate(pageCount.Value) || total is null || total < 0 || total > 200
                || total != decimal.Truncate(total.Value) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 100)
                throw new InvalidDataException("FusionSolar plant pagination is invalid.");
            foreach (var station in list.EnumerateArray())
            {
                var id = Text(station, "plantCode");
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Contains(',') || id.Any(char.IsControl) || !stations.Add(id))
                    throw new InvalidDataException("FusionSolar plant identity is invalid.");
            }
            if (page >= pageCount)
            {
                if (stations.Count != total) throw new InvalidDataException("FusionSolar plant count is inconsistent.");
                break;
            }
            if (list.GetArrayLength() == 0) throw new InvalidDataException("FusionSolar pagination did not advance.");
        }
        var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var station in stations)
        {
            if (parent is not null && parent != station) continue;
            var list = await PostAsync("getDevList", new { stationCodes = station }, ct);
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 200)
                throw new InvalidDataException("FusionSolar device inventory exceeds its bound.");
            foreach (var device in list.EnumerateArray())
            {
                var id = Text(device, "id");
                if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out _) || Text(device, "stationCode") != station
                    || found.Count >= 200 || !found.TryAdd(id, device.Clone()))
                    throw new InvalidDataException("FusionSolar device identity is invalid.");
            }
        }
        if (parent is null) _devices.Clear();
        foreach (var item in found) _devices[item.Key] = item.Value;
        return found.Where(x => IsInverter(x.Value)).Select(x =>
        {
            var battery = Associated(x.Value, 39, "batteryDeviceId");
            var meter = Associated(x.Value, 17, "gridDeviceId");
            return new IntegrationDiscoveredDevice(x.Key, null, "inverter", Text(x.Value, "devName") ?? x.Key, null,
                IntegrationJson.Element(new { stationId = Text(x.Value, "stationCode"), serialNumber = Text(x.Value, "esnCode"), model = Text(x.Value, "model"),
                    capabilities = new { hasBattery = battery.HasValue, hasSolarPower = true, hasSignedGridPower = meter.HasValue && GridSign != 0,
                        hasLoadPower = false, hasGridPowerHistory = meter.HasValue && GridSign != 0, solarBasis = "PvDc" } }));
        }).ToList();
    }
    private static bool IsInverter(JsonElement device) => Number(device, "devTypeId") is 1 or 38;
    private JsonElement? Associated(JsonElement inverter, int type, string configField)
    {
        var station = Text(inverter, "stationCode");
        if (Value(configField) is { Length: > 0 } explicitId)
            return _devices.TryGetValue(explicitId, out var selected) && Number(selected, "devTypeId") == type
                && Text(selected, "stationCode") == station ? selected : null;
        // Device List has no inverter-parent relationship. Only a unique plant topology can be associated automatically.
        if (_devices.Values.Count(x => Text(x, "stationCode") == station && IsInverter(x)) != 1) return null;
        var matches = _devices.Values.Where(x => Text(x, "stationCode") == station && Number(x, "devTypeId") == type).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private async Task<JsonElement> DeviceAsync(string remoteId, CancellationToken ct)
    {
        if (!_devices.TryGetValue(remoteId, out var device))
        {
            await DiscoverAsync(null, ct);
            if (!_devices.TryGetValue(remoteId, out device)) throw new InvalidDataException("FusionSolar device is outside the authorized inventory.");
        }
        if (!IsInverter(device)) throw new InvalidDataException("FusionSolar selected device is not a supported inverter.");
        return device;
    }
    private async Task<JsonElement> RealtimeAsync(JsonElement device, CancellationToken ct)
    {
        var id = Text(device, "id");
        var data = await PostAsync("getDevRealKpi", new { devIds = id, devTypeId = (int)Number(device, "devTypeId")!.Value }, ct);
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 1 || Text(data[0], "devId") != id)
            throw new InvalidDataException("FusionSolar returned a different or missing device.");
        return data[0].GetProperty("dataItemMap").Clone();
    }
    private async Task<ProviderInverterTelemetry> ReadAsync(string remoteId, CancellationToken ct)
    {
        var inverter = await DeviceAsync(remoteId, ct);
        var solar = await RealtimeAsync(inverter, ct);
        var batteryDevice = Associated(inverter, 39, "batteryDeviceId");
        var meterDevice = Associated(inverter, 17, "gridDeviceId");
        JsonElement battery = default, meter = default;
        if (batteryDevice.HasValue) battery = await RealtimeAsync(batteryDevice.Value, ct);
        if (meterDevice.HasValue) meter = await RealtimeAsync(meterDevice.Value, ct);
        // getDevRealKpi does not supply a collection timestamp. params.currentTime is a server response time.
        return new(remoteId, DateTimeOffset.UtcNow, "PvDc", Measure(battery, "battery_soc", null, 0, 100),
            Signed(battery, "ch_discharge_power", BatterySign),
            Missing(), Measure(battery, "busbar_u", null, 0, 2000), Missing(),
            Measure(solar, "mppt_power", null, 0, int.MaxValue, 1000),
            Signed(meter, "active_power", GridSign), Missing());
    }
    private async Task<ProviderGridHistory> HistoryAsync(string remoteId, JsonElement parameters, CancellationToken ct)
    {
        var (start, end) = HistoryInterval(parameters);
        var inverter = await DeviceAsync(remoteId, ct);
        var meter = Associated(inverter, 17, "gridDeviceId");
        if (!meter.HasValue || GridSign == 0) throw new InvalidOperationException("A verified plant grid meter and direction are required for history.");
        var meterId = Text(meter.Value, "id");
        var samples = new SortedDictionary<DateTimeOffset, int>();
        var complete = true;
        for (var cursor = start; cursor < end; cursor = cursor.AddDays(3))
        {
            var stop = cursor.AddDays(3) < end ? cursor.AddDays(3) : end;
            var data = await PostAsync("getDevHistoryKpi", new { devIds = meterId, devTypeId = 17,
                startTime = cursor.ToUnixTimeMilliseconds(), endTime = stop.ToUnixTimeMilliseconds() }, ct);
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 1000) throw new InvalidDataException("FusionSolar history is invalid.");
            if (data.GetArrayLength() == 0) complete = false;
            foreach (var point in data.EnumerateArray())
            {
                if (Text(point, "devId") != meterId) throw new InvalidDataException("FusionSolar returned another device's history.");
                var observed = EpochMilliseconds(point, "collectTime");
                var power = Measure(point.GetProperty("dataItemMap"), "active_power", observed, int.MinValue, int.MaxValue, GridSign);
                if (observed is null || power.Quality != ProviderMeasurementQuality.Good || power.Value != decimal.Truncate(power.Value ?? 0))
                { complete = false; continue; }
                if (observed >= start && observed < end)
                {
                    if (samples.TryGetValue(observed.Value, out var prior) && prior != (int)power.Value!.Value) complete = false;
                    else samples[observed.Value] = (int)power.Value!.Value;
                }
            }
        }
        return new(remoteId, samples.Select(x => new ProviderGridSample(x.Key, x.Value)).ToArray(), null, complete);
    }
    public override async Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        if (Value("authMode") is not (null or "apiAccount" or "accessToken")) throw new ArgumentException("Unsupported FusionSolar authentication mode.");
        if (OAuth ? string.IsNullOrWhiteSpace(Secret("accessToken")) : string.IsNullOrWhiteSpace(Value("username")) || string.IsNullOrWhiteSpace(Secret("systemCode")))
        {
            if (method == "test") return Connection(false, "authorization_required", "Supply a northbound API account or an approved owner OAuth access token.");
            throw new InvalidOperationException("FusionSolar authorization is required.");
        }
        if (method == "test") { await DiscoverAsync(null, ct); return Connection(true, "connected", "FusionSolar authorized inventory verified."); }
        if (method == "discover") return IntegrationJson.Element(await DiscoverAsync(Text(parameters, "parentId"), ct));
        var remoteId = Identity(parameters);
        if (method == "inverter.read") return IntegrationJson.Element(await ReadAsync(remoteId, ct));
        if (method == "inverter.history") return IntegrationJson.Element(await HistoryAsync(remoteId, parameters, ct));
        throw new NotSupportedException("Unsupported FusionSolar operation.");
    }
}

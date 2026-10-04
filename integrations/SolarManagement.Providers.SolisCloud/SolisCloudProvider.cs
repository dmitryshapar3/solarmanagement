using SolarManagement.Integrations.WorkerSdk;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Providers.SolisCloud;

/// <summary>Solis' documented, owner-authorized OAuth data endpoints. No device controls.</summary>
public sealed class SolisCloudProvider : CloudInverterProvider
{
    public const string ProviderId = "solis.cloud";
    public static IReadOnlyList<string> Operations { get; } = ["test", "discover", "inverter.read", "inverter.history"];
    public override int MinimumOperationTimeoutSeconds => 120;
    private readonly Dictionary<string, string> _stations = new(StringComparer.Ordinal);
    public SolisCloudProvider(WorkerConfiguration configuration) : this(configuration, (HttpClient?)null) { }
    public SolisCloudProvider(WorkerConfiguration configuration, HttpClient? http)
        : this(configuration, CloudProviderTransport.Create(configuration, http)) { }
    public SolisCloudProvider(WorkerConfiguration configuration, ICloudJsonTransport transport)
        : base(configuration, transport, "https://api-oauth2.soliscloud.com") { }

    private async Task<JsonElement> PostAsync(string operation, object body, CancellationToken ct)
    {
        var request = JsonPost("/api/access_data/" + operation, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequiredSecret("accessToken"));
        var result = await SendAsync(request, ct);
        if (!result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True || Number(result, "code") != 0
            || !result.TryGetProperty("data", out var data)) throw new InvalidDataException("Solis rejected the data request.");
        return data.Clone();
    }
    private async Task<List<IntegrationDiscoveredDevice>> DiscoverAsync(string? parentId, CancellationToken ct)
    {
        var devices = new List<IntegrationDiscoveredDevice>();
        long? cursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var pageIndex = 0; pageIndex < 3; pageIndex++)
        {
            var body = new Dictionary<string, object?> { ["pageSize"] = 100 };
            if (cursor.HasValue) body["minId"] = cursor.Value;
            if (parentId is not null) body["stationId"] = parentId;
            var data = await PostAsync("inverterList", body, ct);
            var records = data.GetProperty("page").GetProperty("records");
            if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > 100) throw new InvalidDataException("Solis inventory is invalid.");
            long last = -1;
            foreach (var item in records.EnumerateArray())
            {
                var remoteId = Text(item, "sn");
                var id = Number(item, "id");
                if (string.IsNullOrWhiteSpace(remoteId) || remoteId.Length > 128 || remoteId.Any(char.IsControl) || !seen.Add(remoteId)
                    || id is null || id < 0 || id != decimal.Truncate(id.Value) || id >= long.MaxValue || cursor.HasValue && id < cursor)
                    throw new InvalidDataException("Solis inventory identity or cursor is invalid.");
                if (devices.Count >= 200) throw new InvalidDataException("Solis discovery exceeds its bounded inventory.");
                last = Math.Max(last, (long)id);
                var station = Text(item, "stationId");
                if (station is not null) _stations[remoteId] = station;
                devices.Add(new(remoteId, null, "inverter", Text(item, "name") ?? remoteId, null,
                    IntegrationJson.Element(new
                    {
                        stationId = station,
                        capabilities = new { hasBattery = Text(item, "productModel") == "2" || Number(item, "type") == 2,
                            hasSolarPower = true, hasSignedGridPower = GridSign != 0, hasLoadPower = true,
                            hasGridPowerHistory = GridSign != 0 && HistoryUnitFactor != 0 && station is not null, solarBasis = "PvDc" }
                    })));
            }
            if (records.GetArrayLength() < 100) return devices;
            if (last < 0 || cursor.HasValue && last < cursor) throw new InvalidDataException("Solis inventory cursor did not advance.");
            cursor = last + 1;
        }
        throw new InvalidDataException("Solis inventory requires additional pages.");
    }
    private decimal GridSign => Sign("gridPositiveDirection", "import", "export");
    private decimal BatterySign => Sign("batteryPositiveDirection", "discharge", "charge");
    private decimal HistoryUnitFactor => Value("historyPowerUnit") switch { "W" => 1, "kW" => 1000, "MW" => 1000000, _ => 0 };
    private ProviderMeasurement DetailPower(JsonElement data, string key, string unitKey, DateTimeOffset? time, bool nonnegative = false, decimal sign = 1)
    {
        // Modern OAuth tables omit power-unit fields; never assume the older display scale.
        if (data.TryGetProperty(unitKey, out var unit) && unit.ValueKind != JsonValueKind.Null)
            return Power(data, key, unitKey, time, nonnegative, sign);
        var factor = Value("livePowerUnit") switch { "W" => 1m, "kW" => 1000m, _ => 0m };
        if (!data.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return Missing(time);
        return factor == 0 || sign == 0 ? Invalid(time) : Measure(data, key, time, nonnegative ? 0 : int.MinValue, int.MaxValue, factor * sign);
    }
    private async Task<JsonElement> DetailAsync(string remoteId, CancellationToken ct)
    {
        var data = await PostAsync("inverterDetail", new { sn = remoteId }, ct);
        if (Text(data, "sn") != remoteId) throw new InvalidDataException("Solis returned a different device.");
        if (Text(data, "stationId") is { } station) _stations[remoteId] = station;
        return data;
    }
    private static ProviderMeasurement Solar(JsonElement data, DateTimeOffset? time)
    {
        decimal total = 0;
        var found = false;
        for (var channel = 1; channel <= 32; channel++)
        {
            if (!data.TryGetProperty("uPv" + channel, out _) && !data.TryGetProperty("iPv" + channel, out _)) continue;
            found = true;
            var voltage = Number(data, "uPv" + channel); var current = Number(data, "iPv" + channel);
            if (voltage is null || current is null || voltage < 0 || voltage > 2000 || current < 0 || current > 1000) return Invalid(time);
            total += voltage.Value * current.Value;
        }
        return !found ? Missing(time) : total > int.MaxValue ? Invalid(time) : new(total, time, ProviderMeasurementQuality.Good);
    }
    private ProviderInverterTelemetry Normalize(string remoteId, JsonElement data)
    {
        var observed = EpochMilliseconds(data, "dataTimestamp");
        var gridKey = data.TryGetProperty("pSum", out _) ? "pSum" : "psum";
        var gridUnitKey = gridKey == "pSum" && data.TryGetProperty("pSumStr", out _) ? "pSumStr" : "psumStr";
        return new(remoteId, DateTimeOffset.UtcNow, "PvDc", Measure(data, "batteryCapacitySoc", observed, 0, 100),
            DetailPower(data, "batteryPower", "batteryPowerStr", observed, sign: BatterySign),
            Missing(observed), Missing(observed), Missing(observed), Solar(data, observed),
            DetailPower(data, gridKey, gridUnitKey, observed, sign: GridSign),
            DetailPower(data, "familyLoadPower", "familyLoadPowerStr", observed, true));
    }
    private async Task<ProviderGridHistory> HistoryAsync(string remoteId, JsonElement parameters, CancellationToken ct)
    {
        var (start, end) = HistoryInterval(parameters);
        if (GridSign == 0 || HistoryUnitFactor == 0) throw new InvalidOperationException("Verified grid direction and history power unit are required.");
        if (!_stations.TryGetValue(remoteId, out var station))
        {
            await DetailAsync(remoteId, ct);
            if (!_stations.TryGetValue(remoteId, out station)) throw new InvalidDataException("Solis plant association is missing.");
        }
        if (!int.TryParse(Value("plantUtcOffsetHours"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) || offset is < -12 or > 14)
            throw new ArgumentException("The plant UTC offset is required for day queries.");
        var samples = new SortedDictionary<DateTimeOffset, int>();
        var complete = true;
        var utcOffset = TimeSpan.FromHours(offset);
        for (var day = start.ToOffset(utcOffset).Date; day <= end.AddTicks(-1).ToOffset(utcOffset).Date; day = day.AddDays(1))
        {
            var points = await PostAsync("stationDay", new { id = station, time = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), timeZone = offset, money = "PLN" }, ct);
            if (points.ValueKind != JsonValueKind.Array || points.GetArrayLength() > 1000) throw new InvalidDataException("Solis history is invalid.");
            if (points.GetArrayLength() == 0) complete = false;
            foreach (var point in points.EnumerateArray())
            {
                var observed = EpochMilliseconds(point, "time");
                var power = Measure(point, "psum", observed, int.MinValue, int.MaxValue, HistoryUnitFactor * GridSign);
                if (observed is null || power.Quality != ProviderMeasurementQuality.Good || power.Value != decimal.Truncate(power.Value ?? 0)) { complete = false; continue; }
                if (observed >= start && observed < end)
                {
                    if (samples.TryGetValue(observed.Value, out var duplicate) && duplicate != (int)power.Value!.Value) complete = false;
                    else samples[observed.Value] = (int)power.Value!.Value;
                }
            }
        }
        return new(remoteId, samples.Select(pair => new ProviderGridSample(pair.Key, pair.Value)).ToArray(), null, complete);
    }
    public override async Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Secret("accessToken")))
        {
            if (method == "test") return Connection(false, "authorization_required", "Supply an owner-authorized SolisCloud access token after API activation.");
            throw new InvalidOperationException("SolisCloud authorization is required.");
        }
        if (method == "test") { await DiscoverAsync(null, ct); return Connection(true, "connected", "SolisCloud authorized inventory verified."); }
        if (method == "discover") return IntegrationJson.Element(await DiscoverAsync(Text(parameters, "parentId"), ct));
        var remoteId = Identity(parameters);
        if (method == "inverter.read") return IntegrationJson.Element(Normalize(remoteId, await DetailAsync(remoteId, ct)));
        if (method == "inverter.history") return IntegrationJson.Element(await HistoryAsync(remoteId, parameters, ct));
        throw new NotSupportedException("Unsupported SolisCloud operation.");
    }
}

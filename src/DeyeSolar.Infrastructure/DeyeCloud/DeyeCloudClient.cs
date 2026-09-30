using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Infrastructure.DeyeCloud;

public class DeyeCloudClient : IInverterDataSource, IExportGridHistorySource
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<DeyeCloudOptions> _options;
    private readonly ILogger<DeyeCloudClient> _logger;
    private TokenState? _token;

    public DeyeCloudClient(
        HttpClient httpClient,
        IOptionsMonitor<DeyeCloudOptions> options,
        ILogger<DeyeCloudClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public void InvalidateToken() => _token = null;

    public async Task<List<DeyeStation>> GetStationsWithDevicesAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        var opts = _options.CurrentValue;
        using var response = await PostAuthenticatedAsync(
            $"{opts.BaseUrl}/station/listWithDevice",
            new { page = 1, size = 50 }, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        EnsureApiSuccess(json, "station/listWithDevice");

        var stations = new List<DeyeStation>();
        if (json.TryGetProperty("stationList", out var list))
        {
            foreach (var item in list.EnumerateArray())
            {
                stations.Add(new DeyeStation(
                    Id: item.GetProperty("id").GetInt64(),
                    Name: item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Address: item.TryGetProperty("locationAddress", out var a) ? a.GetString() : null
                ));
            }
        }

        return stations;
    }

    public async Task<List<DeyeDevice>> GetDevicesForStationAsync(long stationId, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        var opts = _options.CurrentValue;
        using var response = await PostAuthenticatedAsync(
            $"{opts.BaseUrl}/station/listWithDevice",
            new { page = 1, size = 50 }, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        EnsureApiSuccess(json, "station/listWithDevice");

        var devices = new List<DeyeDevice>();
        if (json.TryGetProperty("stationList", out var stationList))
        {
            foreach (var station in stationList.EnumerateArray())
            {
                if (station.GetProperty("id").GetInt64() != stationId) continue;
                if (station.TryGetProperty("deviceListItems", out var deviceList))
                {
                    foreach (var d in deviceList.EnumerateArray())
                    {
                        devices.Add(new DeyeDevice(
                            SerialNumber: d.TryGetProperty("deviceSn", out var sn) ? sn.GetString() ?? "" : "",
                            DeviceType: d.TryGetProperty("deviceType", out var dt) ? dt.GetString() ?? "" : "",
                            DeviceId: d.TryGetProperty("deviceId", out var di) ? di.GetInt64() : 0,
                            StationId: stationId
                        ));
                    }
                }
            }
        }

        return devices;
    }

    public async Task<InverterData> ReadCurrentDataAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(opts.DeviceSn) || opts.DeviceSn.Length > 128)
            throw new InvalidOperationException("DeviceSn not configured. Go to Settings and select a device.");

        await EnsureTokenAsync(ct);

        using var response = await PostAuthenticatedAsync(
            $"{opts.BaseUrl}/device/latest",
            new { deviceList = new[] { opts.DeviceSn } }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            LogHttpFailure("device/latest", response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"DeyeCloud API error {(int)response.StatusCode}: {errorBody}",
                null,
                response.StatusCode);
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        EnsureApiSuccess(json, "device/latest");

        if (!json.TryGetProperty("deviceDataList", out var deviceDataList))
            throw new InvalidOperationException("No deviceDataList in response");

        var dataMap = new Dictionary<string, string>();
        JsonElement? selectedDevice = null;
        foreach (var device in deviceDataList.EnumerateArray())
        {
            if (device.TryGetProperty("deviceSn", out var serial) &&
                serial.ValueKind == JsonValueKind.String && serial.GetString() == opts.DeviceSn)
                selectedDevice = device;
        }

        if (selectedDevice is not { } selected)
            throw new InvalidOperationException("No data for the configured Deye device in response");

        string? solarUnit = null;
        string? gridUnit = null;
        var gridPointCount = 0;
        if (selected.TryGetProperty("dataList", out var dataList) && dataList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataList.EnumerateArray())
            {
                if (!item.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String)
                    continue;
                var key = keyElement.GetString()!;
                if (key == "TotalGridPower") gridPointCount++;
                if (!item.TryGetProperty("value", out var value) ||
                    value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                    continue;
                dataMap[key] = value.ToString();
                if (key == "TotalSolarPower" && item.TryGetProperty("unit", out var unit))
                    solarUnit = unit.ToString();
                if (key == "TotalGridPower" && item.TryGetProperty("unit", out var gridUnitElement))
                    gridUnit = gridUnitElement.ToString();
            }
        }

        var polledAt = DateTimeOffset.UtcNow;
        var validSolar = dataMap.TryGetValue("TotalSolarPower", out var solarValue) &&
            double.TryParse(solarValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var solarPower) &&
            double.IsFinite(solarPower) && solarPower >= 0 && solarPower <= int.MaxValue &&
            (string.IsNullOrWhiteSpace(solarUnit) || solarUnit.Equals("W", StringComparison.OrdinalIgnoreCase));
        var solarObservedAt = validSolar ? ReadCollectionTime(selected, polledAt) : null;
        dataMap.TryGetValue("TotalGridPower", out var gridValue);
        var validGrid = gridPointCount == 1 && TryReadGridWatts(gridValue, gridUnit, out _);
        var gridObservedAt = validGrid ? ReadCollectionTime(selected, polledAt) : null;

        return new InverterData
        {
            BatterySoc = GetInt(dataMap, "SOC", "BMSSOC"),
            BatteryTemperature = GetDouble(dataMap, "Temperature- Battery"),
            BatteryVoltage = GetDouble(dataMap, "BatteryVoltage", "BMSVoltage"),
            BatteryPower = GetInt(dataMap, "BatteryPower"),
            BatteryCurrent = GetDouble(dataMap, "BMSCurrent"),
            SolarProduction = validSolar ? (int)GetDouble(dataMap, "TotalSolarPower") : 0,
            SolarObservedAt = solarObservedAt,
            SolarDeviceSn = solarObservedAt.HasValue ? opts.DeviceSn : null,
            GridConsumption = TryReadGridWatts(gridValue, gridUnit, out var gridWatts)
                ? gridWatts : GetInt(dataMap, "TotalGridPower"),
            GridObservedAt = gridObservedAt,
            GridDeviceSn = gridObservedAt.HasValue ? opts.DeviceSn : null,
            LoadPower = GetInt(dataMap, "TotalConsumptionPower"),
            Timestamp = polledAt
        };
    }

    public async Task<IReadOnlyList<ExportGridSample>> ReadAsync(string deviceSn, DateTimeOffset start,
        DateTimeOffset end, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var opts = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(deviceSn) || deviceSn.Length > 128 || deviceSn != opts.DeviceSn)
            throw new ArgumentException("Grid history requires the configured device.", nameof(deviceSn));
        if (end <= start || end - start > TimeSpan.FromDays(1) || start.ToUnixTimeSeconds() < 946684800
            || end > DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(end), "Grid history requires a completed interval of at most 24 hours.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await EnsureTokenAsync(deadline.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{opts.BaseUrl}/device/historyRaw")
            {
                Content = JsonContent.Create(new
                {
                    deviceSn, startTimestamp = start.ToUnixTimeSeconds(), endTimestamp = end.ToUnixTimeSeconds(),
                    measurePoints = new[] { "TotalGridPower" }
                })
            };
            // A history fetch can overlap polling; its token belongs to this request, not shared default headers.
            request.Headers.Authorization = new("Bearer", AccessTokenFor(request.RequestUri!.AbsoluteUri));
            using var response = await _httpClient.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Deye grid history returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token);
            if (!json.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("Deye grid history request was not successful.");
            if (deviceSn != _options.CurrentValue.DeviceSn || !json.TryGetProperty("deviceSn", out var serial)
                || serial.ValueKind != JsonValueKind.String || serial.GetString() != deviceSn)
                throw new InvalidDataException("Deye grid history device does not match the selected device.");
            if (!json.TryGetProperty("dataList", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 300)
                throw new InvalidDataException("Deye grid history returned an invalid or unbounded timeline.");

            var samples = new SortedDictionary<DateTimeOffset, int>();
            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("time", out var time) || !long.TryParse(time.ToString(),
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                    || seconds < start.ToUnixTimeSeconds() || seconds >= end.ToUnixTimeSeconds()
                    || !row.TryGetProperty("itemList", out var items) || items.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String || key.GetString() != "TotalGridPower"
                        || !item.TryGetProperty("value", out var value) || !item.TryGetProperty("unit", out var unit)
                        || !TryReadGridWatts(value.ToString(), unit.ToString(), out var watts))
                        continue;
                    var timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    if (timestamp < start || timestamp >= end) continue;
                    if (samples.TryGetValue(timestamp, out var previous) && previous != watts)
                        throw new InvalidDataException("Deye grid history contains conflicting readings at one measurement time.");
                    samples[timestamp] = watts;
                }
            }
            return samples.Select(pair => new ExportGridSample(pair.Key, pair.Value)).ToArray();
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Deye grid history request timed out.", ex);
        }
    }

    private static bool TryReadGridWatts(string? value, string? unit, out int watts)
    {
        watts = 0;
        if (!string.Equals(unit, "W", StringComparison.OrdinalIgnoreCase)
            || !double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number) || number < int.MinValue || number > int.MaxValue)
            return false;
        watts = (int)number;
        return true;
    }

    private static DateTimeOffset? ReadCollectionTime(JsonElement device, DateTimeOffset polledAt)
    {
        // Deye /device/latest collectionTime is Unix seconds, not the time of this HTTP request.
        // https://developer.deyecloud.com/openmcp/docs/deye-open-mcp-tools.html#device-latest
        if (!device.TryGetProperty("collectionTime", out var value) ||
            !long.TryParse(value.ToString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 946684800 || seconds > polledAt.ToUnixTimeSeconds())
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private async Task<HttpResponseMessage> PostAuthenticatedAsync(string uri, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new("Bearer", AccessTokenFor(uri));
        return await _httpClient.SendAsync(request, ct);
    }

    private string AccessTokenFor(string uri)
    {
        var options = _options.CurrentValue;
        var token = _token;
        var configured = new Uri(options.BaseUrl, UriKind.Absolute);
        var requested = new Uri(uri, UriKind.Absolute);
        if (token is null || token.Configuration != AuthenticationIdentity(options)
            || !string.Equals(requested.Scheme, configured.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(requested.Host, configured.Host, StringComparison.OrdinalIgnoreCase)
            || requested.Port != configured.Port || requested.UserInfo != configured.UserInfo
            || !requested.AbsolutePath.StartsWith(configured.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("Deye authentication settings changed during the request. Try again.");
        return token.Value;
    }

    private static string AuthenticationIdentity(DeyeCloudOptions options) => HashPassword(JsonSerializer.Serialize(new
    { options.BaseUrl, options.AppId, options.AppSecret, options.Email, options.Password }));

    private sealed record TokenState(string Value, DateTimeOffset ExpiresAt, string Configuration);

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var configuration = AuthenticationIdentity(opts);
        if (_token is { } token && DateTimeOffset.UtcNow < token.ExpiresAt && token.Configuration == configuration)
            return;
        var passwordHash = HashPassword(opts.Password);

        var tokenRequest = new
        {
            appSecret = opts.AppSecret,
            email = opts.Email,
            password = passwordHash
        };

        using var response = await _httpClient.PostAsJsonAsync(
            $"{opts.BaseUrl}/account/token?appId={opts.AppId}", tokenRequest, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            LogHttpFailure("token", response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"DeyeCloud auth error {(int)response.StatusCode}: {errorBody}",
                null,
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var isSuccess = result.TryGetProperty("success", out var sp) && sp.GetBoolean();
        if (!isSuccess)
        {
            var errCode = result.TryGetProperty("code", out var c) ? c.ToString() : "?";
            var msg = result.TryGetProperty("msg", out var m) ? m.GetString() : "Unknown error";
            throw new InvalidOperationException($"DeyeCloud auth failed: code={errCode}, msg={msg}");
        }

        var accessToken = result.TryGetProperty("accessToken", out var at) ? at.GetString()
            : throw new InvalidOperationException("No accessToken in response");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("No accessToken in response");

        long expiresIn = 3600;
        if (result.TryGetProperty("expiresIn", out var ei))
        {
            if (ei.ValueKind == JsonValueKind.Number) expiresIn = ei.GetInt64();
            else if (ei.ValueKind == JsonValueKind.String && long.TryParse(ei.GetString(), out var parsed))
                expiresIn = parsed;
        }
        if (configuration != AuthenticationIdentity(_options.CurrentValue))
            throw new InvalidOperationException("Deye authentication settings changed during authentication. Try again.");
        _token = new(accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60), configuration);
    }

    private static void EnsureApiSuccess(JsonElement json, string endpoint)
    {
        var isSuccess = json.TryGetProperty("success", out var sp) && sp.GetBoolean();
        if (!isSuccess)
        {
            var code = json.TryGetProperty("code", out var c) ? c.ToString() : "?";
            var msg = json.TryGetProperty("msg", out var m) ? m.GetString() : "Unknown error";
            throw new InvalidOperationException($"DeyeCloud {endpoint} failed: code={code}, msg={msg}");
        }
    }

    private void LogHttpFailure(string operation, HttpStatusCode statusCode, string body)
    {
        if (IsTransientStatusCode(statusCode))
        {
            _logger.LogWarning(
                "DeyeCloud {Operation} returned transient HTTP {Status}: {Body}",
                operation,
                (int)statusCode,
                body);
            return;
        }

        _logger.LogError(
            "DeyeCloud {Operation} returned HTTP {Status}: {Body}",
            operation,
            (int)statusCode,
            body);
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
        => statusCode is
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int GetInt(Dictionary<string, string> data, params string[] keys)
    {
        foreach (var key in keys)
            if (data.TryGetValue(key, out var v) && int.TryParse(v, out var result))
                return result;
        return 0;
    }

    private static double GetDouble(Dictionary<string, string> data, params string[] keys)
    {
        foreach (var key in keys)
            if (data.TryGetValue(key, out var v) && double.TryParse(v,
                System.Globalization.CultureInfo.InvariantCulture, out var result))
                return result;
        return 0;
    }
}

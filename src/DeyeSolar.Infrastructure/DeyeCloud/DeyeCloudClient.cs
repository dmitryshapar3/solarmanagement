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

public class DeyeCloudClient : IInverterDataSource
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<DeyeCloudOptions> _options;
    private readonly ILogger<DeyeCloudClient> _logger;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry;

    public DeyeCloudClient(
        HttpClient httpClient,
        IOptionsMonitor<DeyeCloudOptions> options,
        ILogger<DeyeCloudClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public void InvalidateToken() => _accessToken = null;

    public async Task<List<DeyeStation>> GetStationsWithDevicesAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        SetAuthHeader();

        var opts = _options.CurrentValue;
        var response = await _httpClient.PostAsJsonAsync(
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
        SetAuthHeader();

        var opts = _options.CurrentValue;
        var response = await _httpClient.PostAsJsonAsync(
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
        if (string.IsNullOrEmpty(opts.DeviceSn))
            throw new InvalidOperationException("DeviceSn not configured. Go to Settings and select a device.");

        await EnsureTokenAsync(ct);
        SetAuthHeader();

        var response = await _httpClient.PostAsJsonAsync(
            $"{opts.BaseUrl}/device/latest",
            new { deviceList = new[] { opts.DeviceSn } }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("DeyeCloud device/latest returned {Status}: {Body}",
                (int)response.StatusCode, errorBody);
            throw new InvalidOperationException($"DeyeCloud API error {(int)response.StatusCode}: {errorBody}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        EnsureApiSuccess(json, "device/latest");

        if (!json.TryGetProperty("deviceDataList", out var deviceDataList))
            throw new InvalidOperationException("No deviceDataList in response");

        var dataMap = new Dictionary<string, string>();
        foreach (var device in deviceDataList.EnumerateArray())
        {
            if (device.TryGetProperty("dataList", out var dataList))
            {
                foreach (var item in dataList.EnumerateArray())
                {
                    var key = item.GetProperty("key").GetString() ?? "";
                    var value = item.TryGetProperty("value", out var v) ? v.GetString() ?? "0" : "0";
                    dataMap[key] = value;
                }
            }
        }

        return new InverterData
        {
            BatterySoc = GetInt(dataMap, "SOC", "BMSSOC"),
            BatteryTemperature = GetDouble(dataMap, "Temperature- Battery"),
            BatteryVoltage = GetDouble(dataMap, "BatteryVoltage", "BMSVoltage"),
            BatteryPower = GetInt(dataMap, "BatteryPower"),
            BatteryCurrent = GetDouble(dataMap, "BMSCurrent"),
            SolarProduction = (int)GetDouble(dataMap, "TotalSolarPower"),
            GridConsumption = GetInt(dataMap, "TotalGridPower"),
            LoadPower = GetInt(dataMap, "TotalConsumptionPower"),
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    private void SetAuthHeader()
    {
        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken != null && DateTimeOffset.UtcNow < _tokenExpiry)
            return;

        var opts = _options.CurrentValue;
        var passwordHash = HashPassword(opts.Password);

        var tokenRequest = new
        {
            appSecret = opts.AppSecret,
            email = opts.Email,
            password = passwordHash
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"{opts.BaseUrl}/account/token?appId={opts.AppId}", tokenRequest, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("DeyeCloud token returned {Status}: {Body}", (int)response.StatusCode, errorBody);
            throw new InvalidOperationException($"DeyeCloud auth error {(int)response.StatusCode}: {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var isSuccess = result.TryGetProperty("success", out var sp) && sp.GetBoolean();
        if (!isSuccess)
        {
            var errCode = result.TryGetProperty("code", out var c) ? c.ToString() : "?";
            var msg = result.TryGetProperty("msg", out var m) ? m.GetString() : "Unknown error";
            throw new InvalidOperationException($"DeyeCloud auth failed: code={errCode}, msg={msg}");
        }

        _accessToken = result.TryGetProperty("accessToken", out var at) ? at.GetString()
            : throw new InvalidOperationException("No accessToken in response");

        long expiresIn = 3600;
        if (result.TryGetProperty("expiresIn", out var ei))
        {
            if (ei.ValueKind == JsonValueKind.Number) expiresIn = ei.GetInt64();
            else if (ei.ValueKind == JsonValueKind.String && long.TryParse(ei.GetString(), out var parsed))
                expiresIn = parsed;
        }
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);
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

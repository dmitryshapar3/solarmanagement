using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Infrastructure.Tuya;

public record TuyaDevice(string Id, string Name, string? Category, bool Online);

public record TuyaDeviceStatus(bool IsOn, int? CurrentPowerW, double? VoltageV, double? CurrentA);

public class TuyaCloudClient : ISocketController
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<TuyaOptions> _options;
    private readonly ILogger<TuyaCloudClient> _logger;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry;

    public TuyaCloudClient(
        HttpClient httpClient,
        IOptionsMonitor<TuyaOptions> options,
        ILogger<TuyaCloudClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public void InvalidateToken() => _accessToken = null;

    public async Task TurnOnAsync(string entityId, CancellationToken ct)
    {
        var deviceId = ResolveDeviceId(entityId);
        await SendCommandAsync(deviceId, "switch_1", true, ct);
        _logger.LogInformation("Tuya: turned ON device {DeviceId}", deviceId);
    }

    public async Task TurnOffAsync(string entityId, CancellationToken ct)
    {
        var deviceId = ResolveDeviceId(entityId);
        await SendCommandAsync(deviceId, "switch_1", false, ct);
        _logger.LogInformation("Tuya: turned OFF device {DeviceId}", deviceId);
    }

    public async Task<bool> GetStateAsync(string entityId, CancellationToken ct)
        => (await GetStatusAsync(entityId, ct)).IsOn;

    public async Task<TuyaDeviceStatus> GetStatusAsync(string entityId, CancellationToken ct)
    {
        var deviceId = ResolveDeviceId(entityId);
        var response = await RequestAsync(HttpMethod.Get, $"/v1.0/devices/{deviceId}/status", null, ct);

        bool isOn = false;
        int? powerW = null;
        double? voltageV = null;
        double? currentA = null;

        if (response.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
        {
            foreach (var status in result.EnumerateArray())
            {
                var code = status.TryGetProperty("code", out var c) ? c.GetString() : "";
                if (!status.TryGetProperty("value", out var value))
                    continue;

                switch (code)
                {
                    case "switch_1":
                        if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                            isOn = value.GetBoolean();
                        break;
                    case "cur_power":
                        // Tuya reports power in 0.1 W units
                        if (value.ValueKind == JsonValueKind.Number)
                            powerW = (int)Math.Round(value.GetDouble() / 10.0);
                        break;
                    case "cur_voltage":
                        // 0.1 V units
                        if (value.ValueKind == JsonValueKind.Number)
                            voltageV = value.GetDouble() / 10.0;
                        break;
                    case "cur_current":
                        // mA
                        if (value.ValueKind == JsonValueKind.Number)
                            currentA = value.GetDouble() / 1000.0;
                        break;
                }
            }
        }

        return new TuyaDeviceStatus(isOn, powerW, voltageV, currentA);
    }

    public async Task<List<DevicePowerInfo>> GetDevicesWithStatusAsync(CancellationToken ct)
    {
        var devices = await GetDevicesAsync(ct);
        var results = new List<DevicePowerInfo>(devices.Count);

        foreach (var d in devices)
        {
            if (!d.Online)
            {
                results.Add(new DevicePowerInfo(d.Id, d.Name, d.Category, false, false, null));
                continue;
            }

            try
            {
                var status = await GetStatusAsync(d.Id, ct);
                results.Add(new DevicePowerInfo(d.Id, d.Name, d.Category, true, status.IsOn, status.CurrentPowerW));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tuya: failed to fetch status for device {DeviceId}", d.Id);
                results.Add(new DevicePowerInfo(d.Id, d.Name, d.Category, true, false, null));
            }
        }

        return results;
    }

    public async Task<List<TuyaDevice>> GetDevicesAsync(CancellationToken ct)
    {
        var response = await RequestAsync(HttpMethod.Get,
            "/v2.0/cloud/thing/device?page_size=20", null, ct);
        var devices = new List<TuyaDevice>();

        if (response.TryGetProperty("result", out var result))
        {
            var list = result.ValueKind == JsonValueKind.Array ? result : default;
            if (list.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in list.EnumerateArray())
                {
                    devices.Add(new TuyaDevice(
                        Id: d.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name: d.TryGetProperty("customName", out var cn) && cn.GetString() != ""
                            ? cn.GetString()!
                            : d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Category: d.TryGetProperty("category", out var cat) ? cat.GetString() : null,
                        Online: d.TryGetProperty("isOnline", out var o) && o.GetBoolean()
                    ));
                }
            }
        }

        _logger.LogInformation("Fetched {Count} Tuya devices", devices.Count);
        return devices;
    }

    private async Task SendCommandAsync(string deviceId, string code, bool value, CancellationToken ct)
    {
        var body = new { commands = new[] { new { code, value } } };
        var json = JsonSerializer.Serialize(body);
        await RequestAsync(HttpMethod.Post, $"/v1.0/devices/{deviceId}/commands", json, ct);
    }

    private async Task<JsonElement> RequestAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        var opts = _options.CurrentValue;
        var url = opts.BaseUrl.TrimEnd('/') + path;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        var stringToSign = BuildStringToSign(method.Method, path, body ?? "", timestamp, _accessToken!);
        var sign = CalcSign(opts.AccessId, opts.AccessSecret, stringToSign, timestamp, _accessToken!);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("client_id", opts.AccessId);
        request.Headers.Add("sign", sign);
        request.Headers.Add("t", timestamp);
        request.Headers.Add("sign_method", "HMAC-SHA256");
        request.Headers.Add("access_token", _accessToken);

        if (body != null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var success = result.TryGetProperty("success", out var s) && s.GetBoolean();
        if (!success)
        {
            var code = result.TryGetProperty("code", out var c) ? c.ToString() : "?";
            var msg = result.TryGetProperty("msg", out var m) ? m.GetString() : "Unknown error";
            throw new InvalidOperationException($"Tuya API error: code={code}, msg={msg}");
        }

        return result;
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken != null && DateTimeOffset.UtcNow < _tokenExpiry)
            return;

        var opts = _options.CurrentValue;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var path = "/v1.0/token?grant_type=1";
        var url = opts.BaseUrl.TrimEnd('/') + path;

        var stringToSign = BuildStringToSign("GET", path, "", timestamp, "");
        var sign = CalcSign(opts.AccessId, opts.AccessSecret, stringToSign, timestamp, "");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("client_id", opts.AccessId);
        request.Headers.Add("sign", sign);
        request.Headers.Add("t", timestamp);
        request.Headers.Add("sign_method", "HMAC-SHA256");

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var success = result.TryGetProperty("success", out var s) && s.GetBoolean();
        if (!success)
        {
            var msg = result.TryGetProperty("msg", out var m) ? m.GetString() : "Auth failed";
            throw new InvalidOperationException($"Tuya auth failed: {msg}");
        }

        var data = result.GetProperty("result");
        _accessToken = data.GetProperty("access_token").GetString();
        var expiresIn = data.GetProperty("expire_time").GetInt32();
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);

        _logger.LogInformation("Tuya token obtained, expires at {Expiry}", _tokenExpiry);
    }

    private static string BuildStringToSign(string method, string path, string body, string timestamp, string accessToken)
    {
        var contentHash = SHA256Hash(body);
        var headers = "";
        var signUrl = path;

        return $"{method}\n{contentHash}\n{headers}\n{signUrl}";
    }

    private static string CalcSign(string accessId, string accessSecret, string stringToSign, string timestamp, string accessToken)
    {
        var message = accessId + accessToken + timestamp + stringToSign;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(accessSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToUpperInvariant();
    }

    private static string SHA256Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string ResolveDeviceId(string entityId)
    {
        if (!string.IsNullOrEmpty(entityId) && entityId.Length > 10)
            return entityId;
        var configId = _options.CurrentValue.DeviceId;
        if (!string.IsNullOrEmpty(configId))
            return configId;
        throw new InvalidOperationException("Tuya DeviceId not configured. Go to Settings and select a device.");
    }
}

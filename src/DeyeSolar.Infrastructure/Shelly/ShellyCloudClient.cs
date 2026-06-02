using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Infrastructure.Shelly;

public record ShellyDeviceStatus(bool IsOn, int? CurrentPowerW);

public class ShellyCloudClient : ISocketController
{
    private const int DefaultSwitchChannel = 0;

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<ShellyOptions> _options;
    private readonly ILogger<ShellyCloudClient> _logger;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public ShellyCloudClient(
        HttpClient httpClient,
        IOptionsMonitor<ShellyOptions> options,
        ILogger<ShellyCloudClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task TurnOnAsync(string entityId, CancellationToken ct)
    {
        var deviceId = ResolveDeviceId(entityId);
        await SetSwitchStateAsync(deviceId, true, ct);
        _logger.LogInformation("Shelly: turned ON device {DeviceId}", deviceId);
    }

    public async Task TurnOffAsync(string entityId, CancellationToken ct)
    {
        var deviceId = ResolveDeviceId(entityId);
        await SetSwitchStateAsync(deviceId, false, ct);
        _logger.LogInformation("Shelly: turned OFF device {DeviceId}", deviceId);
    }

    public async Task<bool> GetStateAsync(string entityId, CancellationToken ct)
        => (await GetStatusAsync(entityId, ct)).IsOn;

    public async Task<ShellyDeviceStatus> GetStatusAsync(string entityId, CancellationToken ct)
    {
        var device = await GetDeviceAsync(ResolveDeviceId(entityId), ct);
        return new ShellyDeviceStatus(device.IsOn, device.CurrentPowerW);
    }

    public async Task<List<DevicePowerInfo>> GetDevicesWithStatusAsync(CancellationToken ct)
    {
        EnsureConfigured();

        try
        {
            var devices = await GetAllDevicesAsync(ct);
            if (devices.Count > 0)
                return devices;
        }
        catch (Exception ex)
        {
            if (string.IsNullOrWhiteSpace(_options.CurrentValue.DeviceId))
                throw new InvalidOperationException(
                    "Shelly DeviceId is not configured and the device discovery call failed. Configure Shelly:DeviceId from the Shelly app or retry Fetch Devices.",
                    ex);

            _logger.LogDebug(ex, "Shelly: all_status discovery failed; falling back to configured device id");
        }

        return [await GetDeviceAsync(_options.CurrentValue.DeviceId, ct)];
    }

    private async Task SetSwitchStateAsync(string deviceId, bool isOn, CancellationToken ct)
    {
        var body = new
        {
            id = deviceId,
            channel = DefaultSwitchChannel,
            on = isOn
        };

        await RequestJsonAsync(HttpMethod.Post, "/v2/devices/api/set/switch", body, ct);
    }

    private async Task<DevicePowerInfo> GetDeviceAsync(string deviceId, CancellationToken ct)
    {
        var body = new
        {
            ids = new[] { deviceId },
            select = new[] { "status", "settings" }
        };

        var result = await RequestJsonAsync(HttpMethod.Post, "/v2/devices/api/get", body, ct);
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
            throw new InvalidOperationException($"Shelly API returned no status for device {deviceId}.");

        return ParseDevice(result[0], DefaultSwitchChannel);
    }

    private async Task<List<DevicePowerInfo>> GetAllDevicesAsync(CancellationToken ct)
    {
        var result = await RequestJsonAsync(
            HttpMethod.Post,
            "/device/all_status?show_info=true&no_shared=true",
            body: null,
            ct);

        if (result.TryGetProperty("isok", out var isOk) && isOk.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException($"Shelly API error: {DescribeShellyError(result)}");

        if (!TryGetNestedProperty(result, out var devicesStatus, "data", "devices_status") ||
            devicesStatus.ValueKind != JsonValueKind.Object)
            return [];

        var devices = new List<DevicePowerInfo>();
        var channel = DefaultSwitchChannel;

        foreach (var property in devicesStatus.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;

            var device = ParseDevice(property.Value, channel, fallbackId: property.Name);
            if (IsSwitchLikeDevice(property.Value, channel, device.Category))
                devices.Add(device);
        }

        _logger.LogInformation("Fetched {Count} Shelly switch device(s)", devices.Count);
        return devices;
    }

    private async Task<JsonElement> RequestJsonAsync(
        HttpMethod method,
        string pathAndQuery,
        object? body,
        CancellationToken ct)
    {
        EnsureConfigured();
        await _requestLock.WaitAsync(ct);

        try
        {
            await WaitForRateLimitAsync(ct);

            var url = BuildUrl(pathAndQuery);
            using var request = new HttpRequestMessage(method, url);

            if (body != null)
                request.Content = JsonContent.Create(body);

            _lastRequestAt = DateTimeOffset.UtcNow;
            using var response = await _httpClient.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Shelly API error {(int)response.StatusCode} {response.ReasonPhrase}: {DescribeErrorContent(content)}");

            if (string.IsNullOrWhiteSpace(content))
                content = "{}";

            try
            {
                using var document = JsonDocument.Parse(content);
                return document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Shelly API returned invalid JSON.", ex);
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task WaitForRateLimitAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(0, _options.CurrentValue.RequestIntervalMilliseconds));
        var elapsed = DateTimeOffset.UtcNow - _lastRequestAt;
        if (elapsed < interval)
            await Task.Delay(interval - elapsed, ct);
    }

    private string BuildUrl(string pathAndQuery)
    {
        var opts = _options.CurrentValue;
        var baseUri = opts.ServerUri.Trim();
        if (!baseUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseUri = "https://" + baseUri;
        }

        var separator = pathAndQuery.Contains('?') ? "&" : "?";
        return $"{baseUri.TrimEnd('/')}{pathAndQuery}{separator}auth_key={WebUtility.UrlEncode(opts.AuthKey)}";
    }

    private void EnsureConfigured()
    {
        var opts = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(opts.ServerUri))
            throw new InvalidOperationException("Shelly ServerUri is not configured.");
        if (string.IsNullOrWhiteSpace(opts.AuthKey))
            throw new InvalidOperationException("Shelly AuthKey is not configured.");
    }

    private string ResolveDeviceId(string entityId)
    {
        if (!string.IsNullOrWhiteSpace(entityId))
            return entityId;

        var configId = _options.CurrentValue.DeviceId;
        if (!string.IsNullOrWhiteSpace(configId))
            return configId;

        throw new InvalidOperationException("Shelly DeviceId is not configured. Go to Settings and select a device.");
    }

    private static DevicePowerInfo ParseDevice(JsonElement state, int channel, string? fallbackId = null)
    {
        var status = state.TryGetProperty("status", out var statusElement) &&
            statusElement.ValueKind == JsonValueKind.Object
                ? statusElement
                : state;

        var settings = state.TryGetProperty("settings", out var settingsElement) &&
            settingsElement.ValueKind == JsonValueKind.Object
                ? settingsElement
                : default;

        var devInfo = status.TryGetProperty("_dev_info", out var devInfoElement) &&
            devInfoElement.ValueKind == JsonValueKind.Object
                ? devInfoElement
                : default;

        var id = ReadString(state, "id")
            ?? ReadString(devInfo, "id")
            ?? fallbackId
            ?? "";
        var code = ReadString(state, "code") ?? ReadString(devInfo, "code");
        var gen = ReadString(state, "gen") ?? ReadString(devInfo, "gen");
        var type = ReadString(state, "type");
        var category = string.Join(" ", new[] { "Shelly", code, gen ?? type }.Where(v => !string.IsNullOrWhiteSpace(v)));
        var name = ExtractName(settings) ?? ExtractName(status) ?? BuildDisplayName(code, id);
        var online = ReadBoolish(state, "online") ?? ReadBoolish(devInfo, "online") ?? true;
        var isOn = ReadSwitchState(status, channel) ?? false;
        var powerW = ReadCurrentPower(status, channel);

        return new DevicePowerInfo(id, name, category, online, isOn, powerW);
    }

    private static bool IsSwitchLikeDevice(JsonElement state, int channel, string? category)
    {
        var status = state.TryGetProperty("status", out var statusElement) &&
            statusElement.ValueKind == JsonValueKind.Object
                ? statusElement
                : state;

        if (TryGetSwitchElement(status, channel, out _) ||
            TryGetArrayElement(status, "relays", channel, out _))
        {
            return true;
        }

        return category?.Contains("PLUG", StringComparison.OrdinalIgnoreCase) == true ||
            category?.Contains("RELAY", StringComparison.OrdinalIgnoreCase) == true ||
            category?.Contains("SPSW", StringComparison.OrdinalIgnoreCase) == true ||
            category?.Contains("SNSW", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool? ReadSwitchState(JsonElement status, int channel)
    {
        if (TryGetSwitchElement(status, channel, out var switchElement))
        {
            var output = ReadBoolish(switchElement, "output");
            if (output.HasValue)
                return output.Value;
        }

        if (TryGetArrayElement(status, "relays", channel, out var relayElement))
        {
            var isOn = ReadBoolish(relayElement, "ison");
            if (isOn.HasValue)
                return isOn.Value;
        }

        return ReadBoolish(status, "output");
    }

    private static int? ReadCurrentPower(JsonElement status, int channel)
    {
        if (TryGetSwitchElement(status, channel, out var switchElement))
        {
            var apower = ReadNumber(switchElement, "apower");
            if (apower.HasValue)
                return (int)Math.Round(apower.Value);
        }

        if (TryGetMeterElement(status, channel, out var meterElement))
        {
            var power = ReadNumber(meterElement, "power");
            if (power.HasValue)
                return (int)Math.Round(power.Value);
        }

        var rootPower = ReadNumber(status, "apower") ?? ReadNumber(status, "power");
        return rootPower.HasValue ? (int)Math.Round(rootPower.Value) : null;
    }

    private static bool TryGetSwitchElement(JsonElement status, int channel, out JsonElement switchElement)
        => status.TryGetProperty($"switch:{channel}", out switchElement) &&
            switchElement.ValueKind == JsonValueKind.Object;

    private static bool TryGetMeterElement(JsonElement status, int channel, out JsonElement meterElement)
    {
        if (TryGetArrayElement(status, "meters", channel, out meterElement))
            return true;

        if (status.TryGetProperty($"meter:{channel}", out meterElement) &&
            meterElement.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (status.TryGetProperty($"emeter:{channel}", out meterElement) &&
            meterElement.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        meterElement = default;
        return false;
    }

    private static bool TryGetArrayElement(JsonElement status, string propertyName, int index, out JsonElement element)
    {
        element = default;
        if (!status.TryGetProperty(propertyName, out var arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array ||
            index < 0 ||
            index >= arrayElement.GetArrayLength())
        {
            return false;
        }

        element = arrayElement[index];
        return element.ValueKind == JsonValueKind.Object;
    }

    private static string? ExtractName(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var directName = ReadString(element, "name");
        if (!string.IsNullOrWhiteSpace(directName))
            return directName;

        if (element.TryGetProperty("sys", out var sys) && sys.ValueKind == JsonValueKind.Object)
        {
            var sysName = ReadString(sys, "name");
            if (!string.IsNullOrWhiteSpace(sysName))
                return sysName;

            if (sys.TryGetProperty("device", out var device) && device.ValueKind == JsonValueKind.Object)
            {
                var deviceName = ReadString(device, "name");
                if (!string.IsNullOrWhiteSpace(deviceName))
                    return deviceName;
            }
        }

        return null;
    }

    private static string BuildDisplayName(string? code, string id)
        => !string.IsNullOrWhiteSpace(code)
            ? $"{code} {id}"
            : $"Shelly {id}";

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static bool? ReadBoolish(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var n) => n != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
            JsonValueKind.String when int.TryParse(value.GetString(), out var n) => n != 0,
            _ => null
        };
    }

    private static double? ReadNumber(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var n) => n,
            JsonValueKind.String when double.TryParse(value.GetString(), out var n) => n,
            _ => null
        };
    }

    private static bool TryGetNestedProperty(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    private static string DescribeShellyError(JsonElement result)
    {
        if (result.TryGetProperty("errors", out var errors))
            return errors.ToString();
        if (result.TryGetProperty("error", out var error))
            return error.ToString();
        return result.ToString();
    }

    private static string DescribeErrorContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "empty response";

        try
        {
            using var document = JsonDocument.Parse(content);
            return DescribeShellyError(document.RootElement);
        }
        catch (JsonException)
        {
            var normalized = content.ReplaceLineEndings(" ").Trim();
            return normalized.Length <= 300 ? normalized : normalized[..300];
        }
    }
}

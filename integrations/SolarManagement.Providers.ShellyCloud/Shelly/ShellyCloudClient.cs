using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Providers.ShellyCloud;

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

    public Task SetChannelStateAsync(string entityId, int channel, bool isOn, CancellationToken ct)
    {
        ValidateChannel(channel);
        return SetSwitchStateAsync(ResolveDeviceId(entityId), isOn, ct, channel);
    }

    public Task<DevicePowerInfo> GetChannelStatusAsync(string entityId, int channel, CancellationToken ct)
    {
        ValidateChannel(channel);
        return GetDeviceAsync(ResolveDeviceId(entityId), ct, channel);
    }

    public async Task<ProviderSocketTelemetry> ReadNormalizedStateAsync(string entityId, int channel, CancellationToken ct)
    {
        ValidateChannel(channel);
        var deviceId = ResolveDeviceId(entityId);
        var raw = await GetDeviceResponseAsync(deviceId, ct);
        return ParseNormalizedState(raw, channel, deviceId);
    }

    private static ProviderSocketTelemetry ParseNormalizedState(JsonElement raw, int channel, string deviceId)
    {
        var status = raw.TryGetProperty("status", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : raw;
        var hasChannel = TryGetSwitchElement(status, channel, out _) || TryGetArrayElement(status, "relays", channel, out _)
            || channel == 0 && status.TryGetProperty("output", out _);
        var online = ReadBoolish(raw, "online");
        if (online is null && status.TryGetProperty("_dev_info", out var info) && info.ValueKind == JsonValueKind.Object)
            online = ReadBoolish(info, "online");
        double? power = null;
        if (TryGetSwitchElement(status, channel, out var switchElement)) power = ReadNumber(switchElement, "apower");
        if (power is null && TryGetMeterElement(status, channel, out var meterElement)) power = ReadNumber(meterElement, "power");
        // Pro 3EM Output Add-on is an actuator at switch:100, while the
        // three-phase meter is em:0. Associate them only for this known layout.
        if (power is null && channel >= 100 && IsPro3Em(raw, status)
            && status.TryGetProperty("em:0", out var energyMeter) && energyMeter.ValueKind == JsonValueKind.Object)
            power = ReadNumber(energyMeter, "total_act_power");
        if (power is null && channel == 0) power = ReadNumber(status, "apower") ?? ReadNumber(status, "power");
        var watts = power is { } number && double.IsFinite(number) && number is >= 0 and <= int.MaxValue
            ? (int?)checked((int)Math.Round(number)) : null;
        return new(deviceId, channel.ToString(System.Globalization.CultureInfo.InvariantCulture),
            hasChannel ? ReadSwitchState(status, channel) : null, online, watts, null, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<ProviderSocketTelemetry>> GetNormalizedInventoryAsync(CancellationToken ct)
    {
        JsonElement result;
        try { result = await RequestJsonAsync(HttpMethod.Post, "/device/all_status?show_info=true&no_shared=true", null, ct); }
        catch (Exception) when (!ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(_options.CurrentValue.DeviceId))
        { return [await ReadNormalizedStateAsync(_options.CurrentValue.DeviceId, 0, ct)]; }
        if (result.TryGetProperty("isok", out var isOk) && isOk.ValueKind == JsonValueKind.False
            || !TryGetNestedProperty(result, out var states, "data", "devices_status") || states.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Shelly discovery did not return an authoritative inventory.");
        var devices = new List<ProviderSocketTelemetry>();
        foreach (var entry in states.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var status = entry.Value.TryGetProperty("status", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : entry.Value;
            var channels = new HashSet<int>();
            foreach (var property in status.EnumerateObject())
                if (property.Name.StartsWith("switch:", StringComparison.Ordinal)
                    && int.TryParse(property.Name[7..], out var channel) && channel is >= 0 and <= 199) channels.Add(channel);
            if (status.TryGetProperty("relays", out var relays) && relays.ValueKind == JsonValueKind.Array)
                for (var channel = 0; channel < Math.Min(64, relays.GetArrayLength()); channel++) channels.Add(channel);
            if (channels.Count == 0 && IsSwitchLikeDevice(entry.Value, 0, ParseDevice(entry.Value, 0, entry.Name).Category)) channels.Add(0);
            foreach (var channel in channels.Order())
            {
                if (devices.Count >= 200) throw new InvalidDataException("Shelly inventory exceeds its resource bound.");
                devices.Add(ParseNormalizedState(entry.Value, channel, ReadString(entry.Value, "id") ?? entry.Name));
            }
        }
        return devices;
    }

    private static void ValidateChannel(int channel)
    {
        if (channel is < 0 or > 199) throw new ArgumentOutOfRangeException(nameof(channel));
    }

    private static bool IsPro3Em(JsonElement raw, JsonElement status)
    {
        var code = ReadString(raw, "code");
        if (code is null && status.TryGetProperty("_dev_info", out var info) && info.ValueKind == JsonValueKind.Object)
            code = ReadString(info, "code");
        return code?.StartsWith("SPEM-003", StringComparison.OrdinalIgnoreCase) == true;
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

    private async Task SetSwitchStateAsync(string deviceId, bool isOn, CancellationToken ct, int channel = DefaultSwitchChannel)
    {
        var body = new
        {
            id = deviceId,
            channel,
            on = isOn
        };

        var result = await RequestJsonAsync(HttpMethod.Post, "/v2/devices/api/set/switch", body, ct);
        if (result.ValueKind == JsonValueKind.Object && (result.TryGetProperty("error", out _)
            || result.TryGetProperty("isok", out var isOk) && isOk.ValueKind == JsonValueKind.False))
            throw new InvalidOperationException("Shelly did not acknowledge the switch command.");
    }

    private async Task<DevicePowerInfo> GetDeviceAsync(string deviceId, CancellationToken ct, int channel = DefaultSwitchChannel)
        => ParseDevice(await GetDeviceResponseAsync(deviceId, ct), channel);

    private async Task<JsonElement> GetDeviceResponseAsync(string deviceId, CancellationToken ct)
    {
        var body = new
        {
            ids = new[] { deviceId },
            select = new[] { "status", "settings" }
        };

        var result = await RequestJsonAsync(HttpMethod.Post, "/v2/devices/api/get", body, ct);
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
            throw new InvalidOperationException($"Shelly API returned no status for device {deviceId}.");

        var device = result[0];
        if (ReadString(device, "id") is { } actual && actual != deviceId)
            throw new InvalidDataException("Shelly returned a different device identity.");
        return device;
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

            if (!response.IsSuccessStatusCode || pathAndQuery == "/v2/devices/api/set/switch" && response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException("Shelly request failed.", null, response.StatusCode);

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
        var output = ReadSwitchState(status, channel);
        var powerW = ReadCurrentPower(status, channel);

        return new DevicePowerInfo(id, name, category, online, output.GetValueOrDefault(), powerW, output.HasValue);
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

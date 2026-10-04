using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.WorkerSdk;

namespace SolarManagement.Providers.TuyaCloud;

public abstract class SocketCloudProviderBase : IIntegrationWorkerProvider
{
    protected sealed record Entry(ProviderSocketTelemetry State, string Name, bool CanMeasurePower, JsonElement Metadata);
    protected readonly HttpClient Http;
    private readonly bool _ownsHttp;
    public int MinimumOperationTimeoutSeconds => 30;
    protected SocketCloudProviderBase(WorkerConfiguration configuration, HttpClient? httpClient)
    {
        if (configuration.MaximumOperationTimeoutSeconds < 30) throw new ArgumentException("The operation deadline must be at least 30 seconds.");
        Http = httpClient ?? CloudHttpClient.Create(configuration.AllowedOrigins);
        _ownsHttp = httpClient is null;
    }
    protected abstract Task<IReadOnlyList<Entry>> InventoryAsync(CancellationToken ct);
    protected abstract Task SwitchAsync(Entry device, bool isOn, CancellationToken ct);
    protected virtual Task<string?> AccountIdentityAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public async Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        if (method is not ("test" or "discover" or "socket.inventory" or "socket.read" or "socket.set")) throw new NotSupportedException("Unsupported operation.");
        var entries = await InventoryAsync(ct);
        if (entries.Count > 200 || entries.Select(e => (e.State.RemoteId, e.State.Channel)).Distinct().Count() != entries.Count)
            throw new InvalidDataException("Cloud returned excessive or duplicate devices.");
        if (method == "test") return IntegrationJson.Element(new IntegrationTestResult(true, "connected", "Authenticated cloud inventory verified.", await AccountIdentityAsync(ct)));
        if (method == "discover")
        {
            var accountIdentity = await AccountIdentityAsync(ct);
            return IntegrationJson.Element(entries.Select(e => new IntegrationDiscoveredDevice(e.State.RemoteId, e.State.Channel, "socket", e.Name, accountIdentity,
                IntegrationJson.Element(new { capabilities = new { canSwitch = true, canMeasurePower = e.CanMeasurePower }, provider = e.Metadata }))).ToArray());
        }
        if (method == "socket.inventory") return IntegrationJson.Element(entries.Select(e => e.State).ToArray());
        var id = Identity(parameters, "remoteId");
        var channel = String(parameters, "channel");
        if (channel is { } c && (c.Length > 128 || c != c.Trim())) throw new ArgumentException("Invalid channel identity.");
        var device = entries.SingleOrDefault(e => e.State.RemoteId == id && e.State.Channel == channel);
        if (method == "socket.read") return IntegrationJson.Element(device?.State ?? throw new InvalidDataException("Device is absent from the authoritative inventory."));
        var commandId = Identity(parameters, "commandId");
        if (!Guid.TryParse(commandId, out var commandGuid) || commandGuid == Guid.Empty) throw new ArgumentException("A valid command identity is required.");
        var isOn = parameters.GetProperty("isOn").GetBoolean();
        if (device is null || device.State.Online == false) return IntegrationJson.Element(new ProviderSocketCommandResult(commandId, "Rejected"));
        await SwitchAsync(device, isOn, ct);
        // An HTTP transport success alone is not evidence of a completed switch operation.
        try
        {
            var observed = (await InventoryAsync(ct)).SingleOrDefault(e => e.State.RemoteId == id && e.State.Channel == channel)?.State;
            return IntegrationJson.Element(new ProviderSocketCommandResult(commandId,
                observed?.IsOn == isOn && observed.Online != false ? "Acknowledged" : "Uncertain", ObservedState: observed));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        { return IntegrationJson.Element(new ProviderSocketCommandResult(commandId, "Uncertain")); }
    }
    protected async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Cloud request failed.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("Cloud response exceeds its bound.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) != 0)
            {
                if (bytes.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("Cloud response exceeds its bound.");
                await bytes.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            using var document = JsonDocument.Parse(bytes.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Cloud response is not an object.");
            return document.RootElement.Clone();
        }
    }
    protected static string Endpoint(WorkerConfiguration configuration, string fallback, IReadOnlyList<string> officialOrigins, string path)
    {
        var endpoint = String(configuration.Configuration.Values, "endpoint") ?? fallback;
        var validated = CloudHttpClient.ValidateEndpoint(endpoint, officialOrigins, path);
        return CloudHttpClient.ValidateEndpoint(validated, configuration.AllowedOrigins, path);
    }
    protected static string Secret(WorkerConfiguration configuration, string key)
    {
        var value = configuration.Configuration.Secrets.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("A required cloud credential is missing or invalid.");
        return value;
    }
    protected static string Identity(JsonElement value, string key)
    {
        var id = String(value, key);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id != id.Trim() || id.Any(char.IsControl)) throw new ArgumentException("Invalid remote identity.");
        return id;
    }
    protected static string? String(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    protected static bool? Boolean(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
    protected static decimal? Number(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? Decimal(value) : null;
    protected static decimal? Decimal(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    protected static int? Watts(decimal? number)
        => number is >= 0 and <= int.MaxValue ? (int?)decimal.ToInt32(decimal.Round(number.Value, 0, MidpointRounding.AwayFromZero)) : null;
    protected static DateTimeOffset? Timestamp(decimal? value, bool milliseconds = false)
    {
        if (value is null || value < 1 || value > (milliseconds ? 253402300799999m : 253402300799m)) return null;
        try { return milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds((long)value.Value) : DateTimeOffset.FromUnixTimeSeconds((long)value.Value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    protected static JsonElement Array(JsonElement element, string key)
        => element.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array ? array : throw new InvalidDataException("Cloud inventory shape is invalid.");
    protected static string Escape(string value) => Uri.EscapeDataString(value);
    public ValueTask DisposeAsync() { if (_ownsHttp) Http.Dispose(); return ValueTask.CompletedTask; }
}

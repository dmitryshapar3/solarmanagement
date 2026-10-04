using System.Text.Json;
using DeyeSolar.Domain.Options;
using DeyeSolar.Infrastructure.Shelly;
using Microsoft.Extensions.Logging.Abstractions;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.WorkerSdk;

namespace SolarManagement.Providers.ShellyCloud;

public class ShellyCloudProvider : IIntegrationWorkerProvider
{
    private readonly HttpClient _http;
    private readonly ShellyCloudClient _client;
    public int MinimumOperationTimeoutSeconds { get; }
    public const string ProviderId = "shelly.cloud";
    public static IReadOnlyList<string> Operations { get; } = ["test", "discover", "socket.read", "socket.set", "socket.inventory"];
    public ShellyCloudProvider(WorkerConfiguration configuration, HttpClient? httpClient = null)
    {
        var options = configuration.Configuration.Values.Deserialize<ShellyOptions>(IntegrationJson.Options) ?? new();
        options.AuthKey = configuration.Configuration.Secrets.GetValueOrDefault("authKey") ?? "";
        options.ServerUri = CloudHttpClient.ValidateEndpoint(options.ServerUri, configuration.AllowedOrigins, "/", allowMissingScheme: true);
        // Discovery can make two throttled cloud requests, each with a 30-second HTTP deadline.
        var maximumInterval = (configuration.MaximumOperationTimeoutSeconds - 60) * 500;
        if (options.RequestIntervalMilliseconds < 1000 || options.RequestIntervalMilliseconds > maximumInterval)
            throw new ArgumentException("Shelly request interval must respect the cloud request limit.");
        MinimumOperationTimeoutSeconds = 60 + (int)Math.Ceiling(2 * options.RequestIntervalMilliseconds / 1000d);
        _http = httpClient ?? CloudHttpClient.Create(configuration.AllowedOrigins);
        _client = new(_http, new FixedOptionsMonitor<ShellyOptions>(options), NullLogger<ShellyCloudClient>.Instance);
    }
    public async Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        if (method is "test" or "discover" or "socket.inventory")
        {
            var devices = await _client.GetNormalizedInventoryAsync(ct);
            if (devices.Count > 200) throw new InvalidDataException("Discovery exceeded its bounded inventory.");
            if (method == "test") return IntegrationJson.Element(new IntegrationTestResult(true, "connected", "Connection verified."));
            if (method == "socket.inventory") return IntegrationJson.Element(devices);
            return IntegrationJson.Element(devices.Select(device => new IntegrationDiscoveredDevice(device.RemoteId, device.Channel, "socket",
                $"Shelly {device.RemoteId} relay {device.Channel}", null,
                IntegrationJson.Element(new { capabilities = new { canSwitch = true, canMeasurePower = device.CurrentPowerWatts.HasValue } }))).ToArray());
        }
        var remoteId = parameters.GetProperty("remoteId").GetString();
        if (string.IsNullOrWhiteSpace(remoteId) || remoteId.Length > 128 || remoteId != remoteId.Trim())
            throw new ArgumentException("Device identity is invalid.");
        var channel = parameters.TryGetProperty("channel", out var element) && element.ValueKind == JsonValueKind.String
            ? int.Parse(element.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (method == "socket.read")
        {
            return IntegrationJson.Element(await _client.ReadNormalizedStateAsync(remoteId, channel, ct));
        }
        if (method == "socket.set")
        {
            var commandId = parameters.GetProperty("commandId").GetString();
            if (!Guid.TryParse(commandId, out var commandGuid) || commandGuid == Guid.Empty) throw new ArgumentException("A command identity is required.");
            await _client.SetChannelStateAsync(remoteId, channel, parameters.GetProperty("isOn").GetBoolean(), ct);
            return IntegrationJson.Element(new ProviderSocketCommandResult(commandId!, "Acknowledged"));
        }
        throw new InvalidOperationException("Unsupported socket operation.");
    }
    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
}

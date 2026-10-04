using System.Text.Json;
using DeyeSolar.Domain.Options;
using DeyeSolar.Infrastructure.DeyeCloud;
using Microsoft.Extensions.Logging.Abstractions;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.WorkerSdk;

await IntegrationWorkerHost.RunAsync("deye.cloud", ["test", "discover", "inverter.read", "inverter.history"],
    configuration => new DeyeProvider(configuration));

internal sealed class DeyeProvider : IIntegrationWorkerProvider
{
    private readonly HttpClient _http;
    private readonly DeyeCloudOptions _options;
    private readonly DeyeCloudClient _client;
    public DeyeProvider(WorkerConfiguration configuration)
    {
        _options = configuration.Configuration.Values.Deserialize<DeyeCloudOptions>(IntegrationJson.Options) ?? new();
        _options.AppSecret = configuration.Configuration.Secrets.GetValueOrDefault("appSecret") ?? "";
        _options.Password = configuration.Configuration.Secrets.GetValueOrDefault("password") ?? "";
        _options.BaseUrl = CloudHttpClient.ValidateEndpoint(_options.BaseUrl, configuration.AllowedOrigins, "/v1.0");
        _http = CloudHttpClient.Create(configuration.AllowedOrigins);
        _client = new(_http, new FixedOptionsMonitor<DeyeCloudOptions>(_options), NullLogger<DeyeCloudClient>.Instance);
    }
    public async Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        if (method == "test")
        {
            await _client.GetStationsWithDevicesAsync(ct);
            return IntegrationJson.Element(new IntegrationTestResult(true, "connected", "Connection verified."));
        }
        if (method == "discover")
        {
            var query = parameters.Deserialize<IntegrationDiscoveryQuery>(IntegrationJson.Options) ?? new();
            var stations = await _client.GetStationsWithDevicesAsync(ct);
            var devices = new List<IntegrationDiscoveredDevice>();
            foreach (var station in stations.Where(station => query.ParentId is null || station.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == query.ParentId))
            {
                foreach (var device in await _client.GetDevicesForStationAsync(station.Id, ct))
                {
                    if (devices.Count >= 200) throw new InvalidDataException("Discovery exceeded its bounded inventory.");
                    devices.Add(new(device.SerialNumber, null, "inverter", device.SerialNumber, null,
                        IntegrationJson.Element(new
                        {
                            stationId = station.Id,
                            deviceType = device.DeviceType,
                            capabilities = new
                            {
                                hasBattery = true,
                                hasSolarPower = true,
                                hasSignedGridPower = true,
                                hasLoadPower = true,
                                hasGridPowerHistory = true,
                                solarBasis = "Unknown"
                            }
                        })));
                }
            }
            return IntegrationJson.Element(devices);
        }
        var remoteId = parameters.GetProperty("remoteId").GetString();
        if (string.IsNullOrWhiteSpace(remoteId) || remoteId.Length > 128 || remoteId != remoteId.Trim())
            throw new ArgumentException("Device identity is invalid.");
        _options.DeviceSn = remoteId;
        if (method == "inverter.read") return IntegrationJson.Element(await _client.ReadNormalizedDataAsync(ct));
        if (method == "inverter.history")
        {
            if (parameters.TryGetProperty("continuationToken", out var continuation) && continuation.ValueKind == JsonValueKind.String)
                throw new ArgumentException("This bounded history source does not use continuation tokens.");
            var start = parameters.GetProperty("start").GetDateTimeOffset();
            var end = parameters.GetProperty("end").GetDateTimeOffset();
            var samples = await _client.ReadAsync(remoteId, start, end, ct);
            return IntegrationJson.Element(new ProviderGridHistory(remoteId,
                samples.Select(sample => new ProviderGridSample(sample.Timestamp, sample.GridPowerWatts)).ToArray(), null, true));
        }
        throw new InvalidOperationException("Unsupported inverter operation.");
    }
    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
}

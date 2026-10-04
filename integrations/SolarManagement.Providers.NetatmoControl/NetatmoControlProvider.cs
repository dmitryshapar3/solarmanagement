using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Providers.NetatmoControl;

public sealed class NetatmoControlProvider : SocketCloudProviderBase
{
    public const string ProviderId = "netatmo.control";
    public static IReadOnlyList<string> Operations { get; } = ["test", "discover", "socket.inventory", "socket.read", "socket.set"];
    private static readonly string[] Origins = ["https://api.netatmo.com"];
    private readonly string _endpoint, _accessToken;
    public NetatmoControlProvider(WorkerConfiguration configuration, HttpClient? httpClient = null) : base(configuration, httpClient)
    {
        _endpoint = Endpoint(configuration, "https://api.netatmo.com/api", Origins, "/api");
        _accessToken = Secret(configuration, "accessToken");
    }
    private async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, _endpoint + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, IntegrationJson.Options), Encoding.UTF8, "application/json");
        var response = await SendAsync(request, ct);
        if (response.TryGetProperty("error", out _) || String(response, "status") != "ok" || !response.TryGetProperty("body", out var result)
            || result.ValueKind != JsonValueKind.Object || result.TryGetProperty("errors", out var errors) && (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0))
            throw new InvalidDataException("Netatmo did not return a successful, complete result.");
        return result;
    }
    protected override async Task<IReadOnlyList<Entry>> InventoryAsync(CancellationToken ct)
    {
        var topology = await RequestAsync(HttpMethod.Get, "/homesdata", null, ct);
        var homes = Array(topology, "homes");
        if (homes.GetArrayLength() > 20) throw new InvalidDataException("Netatmo home inventory exceeds its bound.");
        var entries = new List<Entry>();
        foreach (var home in homes.EnumerateArray())
        {
            var homeId = Identity(home, "id");
            var modules = Array(home, "modules");
            if (modules.GetArrayLength() > 200) throw new InvalidDataException("Netatmo module inventory exceeds its bound.");
            var eligible = modules.EnumerateArray().Where(m => String(m, "type") is "NLP" or "NLPM" or "NLC" or "NLPO").ToArray();
            if (eligible.Length == 0) continue;
            var result = await RequestAsync(HttpMethod.Get, "/homestatus?home_id=" + Escape(homeId), null, ct);
            var statusHome = result.GetProperty("home");
            if (String(statusHome, "id") != homeId) throw new InvalidDataException("Netatmo returned a different home identity.");
            var statusModules = Array(statusHome, "modules");
            if (statusModules.GetArrayLength() > 200) throw new InvalidDataException("Netatmo status inventory exceeds its bound.");
            var statuses = statusModules.EnumerateArray().ToDictionary(m => Identity(m, "id"), StringComparer.Ordinal);
            foreach (var module in eligible)
            {
                if (entries.Count >= 200) throw new InvalidDataException("Netatmo socket inventory exceeds its bound.");
                var id = Identity(module, "id");
                var bridge = Identity(module, "bridge");
                statuses.TryGetValue(id, out var status);
                if (status.ValueKind == JsonValueKind.Object && String(status, "type") != String(module, "type")) throw new InvalidDataException("Netatmo module identity changed.");
                entries.Add(new(new(id, null, Boolean(status, "on"), Boolean(status, "reachable"), Watts(Number(status, "power")),
                    Timestamp(Number(status, "last_seen")), DateTimeOffset.UtcNow), String(module, "name") ?? id, true,
                    IntegrationJson.Element(new { homeId, bridge, model = String(module, "type"), phases = (int?)null })));
            }
        }
        return entries;
    }
    protected override async Task SwitchAsync(Entry device, bool isOn, CancellationToken ct)
    {
        var homeId = Identity(device.Metadata, "homeId");
        var bridge = Identity(device.Metadata, "bridge");
        await RequestAsync(HttpMethod.Post, "/setstate", new { home = new { id = homeId, modules = new[] { new { id = device.State.RemoteId, on = isOn, bridge } } } }, ct);
    }
}

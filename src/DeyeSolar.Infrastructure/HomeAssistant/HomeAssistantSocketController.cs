using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Infrastructure.HomeAssistant;

public class HomeAssistantSocketController : ISocketController
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<HomeAssistantOptions> _options;
    private readonly ILogger<HomeAssistantSocketController> _logger;

    public HomeAssistantSocketController(
        HttpClient httpClient,
        IOptionsMonitor<HomeAssistantOptions> options,
        ILogger<HomeAssistantSocketController> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task TurnOnAsync(string entityId, CancellationToken ct)
    {
        _logger.LogInformation("Turning ON {EntityId}", entityId);
        await CallServiceAsync("switch/turn_on", entityId, ct);
    }

    public async Task TurnOffAsync(string entityId, CancellationToken ct)
    {
        _logger.LogInformation("Turning OFF {EntityId}", entityId);
        await CallServiceAsync("switch/turn_off", entityId, ct);
    }

    public async Task<bool> GetStateAsync(string entityId, CancellationToken ct)
    {
        ConfigureClient();
        var response = await _httpClient.GetAsync($"api/states/{entityId}", ct);
        response.EnsureSuccessStatusCode();

        var state = await response.Content.ReadFromJsonAsync<HaStateResponse>(cancellationToken: ct);
        return string.Equals(state?.State, "on", StringComparison.OrdinalIgnoreCase);
    }

    private async Task CallServiceAsync(string service, string entityId, CancellationToken ct)
    {
        ConfigureClient();
        var payload = new { entity_id = entityId };
        var response = await _httpClient.PostAsJsonAsync($"api/services/{service}", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    private void ConfigureClient()
    {
        var opts = _options.CurrentValue;
        _httpClient.BaseAddress ??= new Uri(opts.BaseUrl.TrimEnd('/') + "/");

        if (!_httpClient.DefaultRequestHeaders.Contains("Authorization"))
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {opts.AccessToken}");
    }

    private class HaStateResponse
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("entity_id")]
        public string EntityId { get; set; } = string.Empty;
    }
}

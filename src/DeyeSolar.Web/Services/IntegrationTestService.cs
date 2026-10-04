using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Api;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

/// <summary>Bounded, read-only probes. Draft settings, tokens and responses never enter storage or logs.</summary>
public sealed class IntegrationTestService(IHttpClientFactory clients,
    IOptionsMonitor<SolarEstimateOptions> solarOptions, TimeProvider clock, IntegrationProbeGate gate) : IIntegrationTestService
{
    public const string ClientName = "IntegrationReadOnlyTest";

    public async Task<IntegrationTestResult> TestAsync(string kind, IntegrationTestRequest request, CancellationToken ct)
    {
        kind = kind.ToLowerInvariant();
        if (kind is not ("openmeteo" or "pse"))
            return Result(kind, false, "configuration", "Configure device manufacturers in the integrations section.");
        if (!await gate.Lock.WaitAsync(0, ct)) return Result(kind, false, "busy", "Another connection test is running. Please try again shortly.");
        try
        {
            if (gate.NextTest.TryGetValue(kind, out var next) && clock.GetUtcNow() < next)
                return Result(kind, false, "busy", "Please wait 10 seconds between connection tests.");
            gate.NextTest[kind] = clock.GetUtcNow().AddSeconds(10);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var client = clients.CreateClient(ClientName);
            var message = kind switch
            {
                "openmeteo" => await WeatherAsync(client, request, timeout.Token),
                _ => await PseAsync(client, timeout.Token)
            };
            return Result(kind, true, "ok", message);
        }
        catch (ProbeConfigurationException) { return Result(kind, false, "configuration", "Check the required fields and the provider's HTTPS server address."); }
        catch (ProbeAuthenticationException) { return Result(kind, false, "authentication", "The provider did not accept these credentials. Check the account and key."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Result(kind, false, "timeout", "The provider did not respond in time. Please try again."); }
        catch (Exception) when (!ct.IsCancellationRequested) { return Result(kind, false, "unavailable", "The provider could not be verified. Check the configuration or try again later."); }
        finally { gate.Lock.Release(); }
    }

    private IntegrationTestResult Result(string kind, bool success, string code, string message) => new(kind, success, code, message, clock.GetUtcNow());

    private async Task<string> WeatherAsync(HttpClient client, IntegrationTestRequest draft, CancellationToken ct)
    {
        var value = solarOptions.CurrentValue;
        var latitude = draft.SolarEstimate?.Latitude ?? value.Latitude;
        var longitude = draft.SolarEstimate?.Longitude ?? value.Longitude;
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90 || !double.IsFinite(longitude) || longitude is < -180 or > 180)
            throw new ProbeConfigurationException();
        var key = value.ApiKey?.Trim();
        var host = string.IsNullOrEmpty(key) ? "api.open-meteo.com" : "customer-api.open-meteo.com";
        var uri = $"https://{host}/v1/forecast?latitude={latitude.ToString("G", CultureInfo.InvariantCulture)}&longitude={longitude.ToString("G", CultureInfo.InvariantCulture)}&current=temperature_2m&timezone=UTC";
        if (!string.IsNullOrEmpty(key)) uri += "&apikey=" + Uri.EscapeDataString(key);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var document = await ReadAsync(client, request, ct);
        if (!document.RootElement.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object
            || !current.TryGetProperty("temperature_2m", out var temperature) || temperature.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException();
        return "Connected. Open-Meteo returned weather data for the configured location.";
    }

    private static async Task<string> PseAsync(HttpClient client, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.raporty.pse.pl/api/rce-pln?$first=1&$orderby=dtime_utc%20desc&$select=dtime_utc,rce_pln");
        using var document = await ReadAsync(client, request, ct);
        if (!document.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException();
        return "Connected. The public PSE electricity price feed was verified.";
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new ProbeAuthenticationException();
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri != request.RequestUri) throw new HttpRequestException("Provider request failed.");
        const int limit = 256 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > limit) throw new InvalidDataException();
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 32 }, ct);
    }
    private sealed class ProbeConfigurationException : Exception { }
    private sealed class ProbeAuthenticationException : Exception { }
}

public sealed class IntegrationProbeGate
{
    internal SemaphoreSlim Lock { get; } = new(1, 1);
    internal ConcurrentDictionary<string, DateTimeOffset> NextTest { get; } = new(StringComparer.Ordinal);
}

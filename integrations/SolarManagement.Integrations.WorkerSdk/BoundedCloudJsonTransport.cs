using System.Text.Json;
using SolarManagement.Http;

namespace SolarManagement.Integrations.WorkerSdk;

public interface ICloudJsonTransport : IAsyncDisposable
{
    Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct, Action<HttpResponseMessage>? observeResponse = null);
}

/// <summary>Owns request disposal, HTTP classification and bounded response parsing for provider adapters.</summary>
public sealed class BoundedCloudJsonTransport(HttpClient http, bool ownsHttp = false) : ICloudJsonTransport
{
    public ValueTask DisposeAsync() { if (ownsHttp) http.Dispose(); return ValueTask.CompletedTask; }
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    public async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct, Action<HttpResponseMessage>? observeResponse = null)
    {
        using (request)
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Cloud request failed.", null, response.StatusCode);
            observeResponse?.Invoke(response);
            byte[] bytes;
            try { bytes = await BoundedHttpContent.ReadBytesAsync(response.Content, MaximumResponseBytes, ct); }
            catch (ResponseTooLargeException) { throw new InvalidDataException("Cloud response exceeds its bound."); }
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
    }
}

public static class CloudProviderTransport
{
    public static ICloudJsonTransport Create(SolarManagement.Integrations.Contracts.WorkerConfiguration configuration, HttpClient? http = null)
        => new BoundedCloudJsonTransport(http ?? CloudHttpClient.Create(configuration.AllowedOrigins), ownsHttp: http is null);
}

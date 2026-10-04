using System.Text.Json;

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
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Cloud response exceeds its bound.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) != 0)
            {
                if (bytes.Length + count > MaximumResponseBytes) throw new InvalidDataException("Cloud response exceeds its bound.");
                await bytes.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            using var document = JsonDocument.Parse(bytes.ToArray());
            return document.RootElement.Clone();
        }
    }
}

public static class CloudProviderTransport
{
    public static ICloudJsonTransport Create(SolarManagement.Integrations.Contracts.WorkerConfiguration configuration, HttpClient? http = null)
        => new BoundedCloudJsonTransport(http ?? CloudHttpClient.Create(configuration.AllowedOrigins), ownsHttp: http is null);
}

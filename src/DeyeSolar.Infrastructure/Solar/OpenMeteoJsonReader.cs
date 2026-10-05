using System.Text.Json;
using SolarManagement.Http;

namespace DeyeSolar.Infrastructure.Solar;

public interface IOpenMeteoJsonReader : IJsonDocumentReader { }

public sealed class OpenMeteoJsonReader(HttpClient http, TimeProvider clock) : IOpenMeteoJsonReader
{
    private readonly RetryingJsonReader _reader = new(http, new JsonReadPolicy("Open-Meteo"), clock);
    public async Task<JsonDocument> ReadAsync(Uri uri, CancellationToken ct)
    {
        try { return await _reader.ReadAsync(uri, ct); }
        catch (ResponseTooLargeException) { throw new InvalidDataException("Provider response exceeded its allowed size."); }
    }
}

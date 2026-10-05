using System.Text.Json;
using SolarManagement.Http;

namespace DeyeSolar.Infrastructure.Settlement;

public interface IPseJsonReader : IJsonDocumentReader { }

public sealed class PseJsonReader(HttpClient http, TimeProvider clock) : IPseJsonReader
{
    private readonly RetryingJsonReader _reader = new(http, new JsonReadPolicy("PSE price", MaxDepth: 16,
        IsResponseUriAllowed: PseExportPriceClient.IsSourceUri), clock);
    public async Task<JsonDocument> ReadAsync(Uri uri, CancellationToken ct)
    {
        try { return await _reader.ReadAsync(uri, ct); }
        catch (ResponseTooLargeException) { throw new InvalidDataException("Provider response exceeded its allowed size."); }
    }
}

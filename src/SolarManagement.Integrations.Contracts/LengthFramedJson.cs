using System.Buffers.Binary;
using System.Text.Json;

namespace SolarManagement.Integrations.Contracts;

public static class LengthFramedJson
{
    public static async Task<JsonElement?> ReadAsync(Stream stream, int maximumBytes, CancellationToken ct)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), ct);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 1 || length > maximumBytes) throw new InvalidDataException("Worker frame exceeds its allowed size.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        using var json = JsonDocument.Parse(body, new() { MaxDepth = 32 });
        return json.RootElement.Clone();
    }
    public static async Task WriteAsync<T>(Stream stream, T value, int maximumBytes, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, IntegrationJson.Options);
        if (body.Length > maximumBytes) throw new InvalidDataException("Worker frame exceeds its allowed size.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }
}

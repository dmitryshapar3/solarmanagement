namespace SolarManagement.Http;

public sealed class ResponseTooLargeException() : IOException("The HTTP response exceeded its allowed size.");

/// <summary>Bounds both declared and streamed bodies before any provider-specific parsing.</summary>
public static class BoundedHttpContent
{
    public static async Task<byte[]> ReadBytesAsync(HttpContent content, int maximumBytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ct.ThrowIfCancellationRequested();
        if (content.Headers.ContentLength > maximumBytes) throw new ResponseTooLargeException();
        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[Math.Min(8192, maximumBytes + (maximumBytes < int.MaxValue ? 1 : 0))];
        int count;
        while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (count > maximumBytes - bytes.Length) throw new ResponseTooLargeException();
            await bytes.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        return bytes.ToArray();
    }
}

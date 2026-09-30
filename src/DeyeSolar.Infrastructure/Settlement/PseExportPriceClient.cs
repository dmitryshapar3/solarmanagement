using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;

namespace DeyeSolar.Infrastructure.Settlement;

/// <summary>
/// Official PSE RCE publication: https://raporty.pse.pl/report/rce-pln.
/// The API's dtime_utc is the quarter-hour's exclusive end, and rce_pln is PLN/MWh.
/// Signed prices are preserved; this source does not apply prosumer settlement rules.
/// </summary>
public sealed class PseExportPriceClient(HttpClient httpClient) : IExportPriceSource
{
    private const string Endpoint = "https://api.raporty.pse.pl/api/rce-pln";
    private const int PageSize = 1000;
    private const int MaximumPages = 40;
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryNotBefore = new();

    public async Task<IReadOnlyList<ExportPriceInterval>> ReadAsync(DateTimeOffset start,
        DateTimeOffset end, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        start = start.ToUniversalTime();
        end = end.ToUniversalTime();
        if (start.Ticks % Interval.Ticks != 0 || end.Ticks % Interval.Ticks != 0
            || end <= start || end - start > TimeSpan.FromDays(367))
            throw new ArgumentException("Export prices require UTC quarter-hour boundaries over a positive range of at most 367 days.");

        var filter = $"dtime_utc gt '{FormatTime(start)}' and dtime_utc le '{FormatTime(end)}'";
        Uri? next = new(Endpoint + "?$filter=" + Uri.EscapeDataString(filter)
            + "&$orderby=dtime_utc&$first=1000&$select=dtime_utc,rce_pln");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ExportPriceInterval>();
        DateTimeOffset? previousEnd = null;
        var maximumRows = (end - start).Ticks / Interval.Ticks;
        var rowCount = 0L;
        var pageCount = 0;
        while (next is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (++pageCount > MaximumPages || !visited.Add(next.AbsoluteUri))
                throw new InvalidDataException("PSE price pagination exceeded its bounded range or repeated a page.");
            using var document = await ReadPageAsync(next, ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var rows)
                || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > PageSize)
                throw new InvalidDataException("PSE returned an invalid price page.");

            foreach (var row in rows.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (++rowCount > maximumRows || row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty("dtime_utc", out var timestamp)
                    || timestamp.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParseExact(timestamp.GetString(), "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var intervalEnd)
                    || intervalEnd.Ticks % Interval.Ticks != 0 || intervalEnd <= start || intervalEnd > end
                    || previousEnd.HasValue && intervalEnd <= previousEnd.Value)
                    throw new InvalidDataException("PSE price timestamps are not ordered quarter-hours within the requested UTC range.");
                previousEnd = intervalEnd;
                // Absence is not a zero price, and must not borrow another interval's price.
                if (!row.TryGetProperty("rce_pln", out var price) || price.ValueKind == JsonValueKind.Null) continue;
                if (price.ValueKind != JsonValueKind.Number || !price.TryGetDecimal(out var value))
                    throw new InvalidDataException("PSE returned an invalid decimal price.");
                result.Add(new(intervalEnd - Interval, intervalEnd, value));
            }

            next = NextPage(root);
        }
        return result.AsReadOnly();
    }

    private static Uri? NextPage(JsonElement root)
    {
        if (!root.TryGetProperty("nextLink", out var link) || link.ValueKind == JsonValueKind.Null) return null;
        if (link.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri)
            || !IsSourceUri(uri))
            throw new InvalidDataException("PSE returned an invalid price pagination address.");
        return uri;
    }

    private static bool IsSourceUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == "api.raporty.pse.pl" && uri.Port == 443 && uri.AbsolutePath == "/api/rce-pln"
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private async Task<JsonDocument> ReadPageAsync(Uri uri, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (_retryNotBefore.TryGetValue(uri.Host, out var deadline) && deadline > DateTimeOffset.UtcNow)
                throw new HttpRequestException("PSE Retry-After window is still active; price refresh deferred.");
            HttpStatusCode? status = null;
            TimeSpan? retryAfter = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.RequestMessage?.RequestUri is { } responseUri && !IsSourceUri(responseUri))
                    throw new InvalidDataException("PSE price response was redirected away from its authoritative endpoint.");
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 16 }, timeout.Token);
                }
                status = response.StatusCode;
                retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (HttpRequestException) { }
            catch (IOException) { }
            catch (JsonException)
            {
                throw new InvalidDataException("PSE returned malformed price JSON.");
            }
            ct.ThrowIfCancellationRequested();
            var transient = status is null or HttpStatusCode.TooManyRequests || (int)status.Value >= 500;
            if (transient && retryAfter > MaximumRetryDelay)
            {
                var retryDeadline = DateTimeOffset.UtcNow + retryAfter.Value;
                _retryNotBefore.AddOrUpdate(uri.Host, retryDeadline, (_, current) => current > retryDeadline ? current : retryDeadline);
            }
            if (!transient || attempt == MaximumAttempts - 1 || retryAfter > MaximumRetryDelay)
                throw new HttpRequestException("PSE price request failed; refresh deferred.", null, status);
            var delay = retryAfter.HasValue ? (retryAfter.Value < TimeSpan.Zero ? TimeSpan.Zero : retryAfter.Value)
                : TimeSpan.FromMilliseconds(250 * (1 << attempt));
            await Task.Delay(delay, ct);
        }
        throw new HttpRequestException("PSE price request failed.");
    }

    private static string FormatTime(DateTimeOffset time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

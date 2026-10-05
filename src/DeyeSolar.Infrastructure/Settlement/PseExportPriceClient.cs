using System.Globalization;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;

namespace DeyeSolar.Infrastructure.Settlement;

/// <summary>
/// Official PSE RCE publication: https://raporty.pse.pl/report/rce-pln.
/// The API's dtime_utc is the quarter-hour's exclusive end, and rce_pln is PLN/MWh.
/// Signed prices are preserved; this source does not apply prosumer settlement rules.
/// </summary>
public sealed class PseExportPriceClient(IPseJsonReader transport) : IExportPriceSource
{
    private const string Endpoint = "https://api.raporty.pse.pl/api/rce-pln";
    private const int PageSize = 1000;
    private const int MaximumPages = 40;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
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
            using var document = await transport.ReadAsync(next, ct);
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

    internal static bool IsSourceUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == "api.raporty.pse.pl" && uri.Port == 443 && uri.AbsolutePath == "/api/rce-pln"
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static string FormatTime(DateTimeOffset time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

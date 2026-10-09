using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;

namespace DeyeSolar.Infrastructure.Settlement;

/// <summary>Bounded CSV/XML reader. Timestamps and currency units must be explicit; missing prices stay missing.</summary>
public sealed class ExportPriceFeedClient(HttpClient http)
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public async Task<IReadOnlyList<ExportPriceInterval>> ReadAsync(string address, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        if (!SolarSalesOptions.IsAllowedFeedUrl(address)) throw new ArgumentException("Enter a public HTTPS CSV/XML URL.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { return await ReadBodyAsync(address, start, end, timeout.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new HttpRequestException("Price feed timed out.", ex); }
    }
    private async Task<IReadOnlyList<ExportPriceInterval>> ReadBodyAsync(string address, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(address), HttpCompletionOption.ResponseHeadersRead, ct);
        // PublicHttpTransport disables redirects and pins public DNS addresses at connect time.
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Price feed exceeds 2 MiB.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await body.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("Price feed exceeds 2 MiB.");
            buffer.Write(bytes, 0, count);
        }
        return Parse(new UTF8Encoding(false, true).GetString(buffer.ToArray()), start, end);
    }

    public static IReadOnlyList<ExportPriceInterval> Parse(string text, DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start || end - start > TimeSpan.FromDays(367) || start.UtcTicks % TimeSpan.FromMinutes(15).Ticks != 0
            || end.UtcTicks % TimeSpan.FromMinutes(15).Ticks != 0) throw new ArgumentException("Price range must use UTC quarter-hours.");
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new InvalidDataException("Price feed exceeds 2 MiB.");
        text = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        var rows = text.StartsWith('<') ? XmlRows(text) : CsvRows(text);
        var all = new SortedDictionary<DateTimeOffset, ExportPriceInterval>();
        var rowsRead = 0;
        foreach (var row in rows)
        {
            if (++rowsRead > 40000) throw new InvalidDataException("Price feed has too many intervals.");
            if (!row.TryGetValue("interval_start", out var fromText) || !row.TryGetValue("interval_end", out var toText))
                throw new InvalidDataException("Feed needs interval_start and interval_end with explicit UTC or offset timestamps.");
            var from = Timestamp(fromText); var to = Timestamp(toText);
            if (to <= from || to - from != TimeSpan.FromMinutes(15) && to - from != TimeSpan.FromHours(1))
                throw new InvalidDataException("Feed intervals must last 15 or 60 minutes.");
            var kwh = row.TryGetValue("price_pln_per_kwh", out var amount);
            var mwh = row.TryGetValue("price_pln_per_mwh", out var marketAmount);
            if (kwh == mwh) throw new InvalidDataException("Use exactly one price_pln_per_kwh or price_pln_per_mwh column.");
            amount = kwh ? amount : marketAmount;
            if (string.IsNullOrWhiteSpace(amount)) continue;
            if (!decimal.TryParse(amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var price)) throw new InvalidDataException("Feed prices require a decimal point and no thousands separators.");
            if (kwh) price *= 1000m;
            if (Math.Abs(price) >= 1_000_000_000_000m || decimal.Round(price, 6) != price)
                throw new InvalidDataException("Feed price exceeds the supported precision or range.");
            for (var cursor = from; cursor < to; cursor = cursor.AddMinutes(15))
            {
                if (!all.TryAdd(cursor, new(cursor, cursor.AddMinutes(15), price)))
                    throw new InvalidDataException("Price feed contains duplicate or overlapping intervals.");
            }
        }
        return all.Values.Where(p => p.Start >= start && p.End <= end).ToArray();
    }
    private static DateTimeOffset Timestamp(string value)
    {
        value = value.Trim();
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        if (!DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            || time.Year < 2000 || time.UtcTicks % TimeSpan.FromMinutes(15).Ticks != 0)
            throw new InvalidDataException("Feed timestamps require ISO 8601 with Z or an explicit offset and quarter-hour boundaries.");
        return time;
    }
    private static IEnumerable<Dictionary<string, string>> XmlRows(string text)
    {
        using (var bounds = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes }))
            while (bounds.Read()) if (bounds.Depth > 4) throw new InvalidDataException("XML price feed is nested too deeply.");
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes, MaxCharactersFromEntities = 0 });
        var document = XDocument.Load(reader);
        if (document.Root is not { Name.LocalName: "prices" } root || root.Name.NamespaceName.Length != 0)
            throw new InvalidDataException("XML feed root must be prices.");
        foreach (var row in root.Elements())
        {
            if (row.Name != "price" || row.HasAttributes || row.Elements().Any(e => e.HasElements || e.HasAttributes || e.Name.NamespaceName.Length != 0)
                || row.Elements().GroupBy(e => e.Name).Any(g => g.Count() != 1))
                throw new InvalidDataException("XML prices must contain plain, unique field elements.");
            yield return row.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value, StringComparer.Ordinal);
        }
    }
    private static IEnumerable<Dictionary<string, string>> CsvRows(string text)
    {
        using var reader = new StringReader(text);
        var header = reader.ReadLine() ?? throw new InvalidDataException("Price feed is empty.");
        var delimiter = header.Contains(';') ? ';' : ',';
        var names = CsvFields(header, delimiter);
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new InvalidDataException("Duplicate CSV columns.");
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = CsvFields(line, delimiter);
            if (fields.Length != names.Length) throw new InvalidDataException("CSV price row does not match its header.");
            yield return names.Select((name, i) => (name, fields[i])).ToDictionary(pair => pair.name, pair => pair.Item2, StringComparer.Ordinal);
        }
    }
    private static string[] CsvFields(string line, char delimiter)
    {
        var result = new List<string>(); var field = new StringBuilder(); var quoted = false; var closed = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') { quoted = false; closed = true; }
                else field.Append(ch);
            }
            else if (ch == delimiter) { result.Add(field.ToString().Trim()); field.Clear(); closed = false; }
            else if (ch == '"' && field.Length == 0 && !closed) quoted = true;
            else if (ch == '"' || closed && !char.IsWhiteSpace(ch)) throw new InvalidDataException("Invalid CSV quoting.");
            else field.Append(ch);
        }
        if (quoted) throw new InvalidDataException("Unclosed CSV quote.");
        result.Add(field.ToString().Trim()); return result.ToArray();
    }
}

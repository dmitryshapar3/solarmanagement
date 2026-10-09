using SolarManagement.Inverters.Contracts;
using System.Security.Cryptography;
using System.Text;
using DeyeSolar.Infrastructure.Settlement;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

public sealed class ExportSalesService(IExportReadingStore readings, IExportGridHistorySource history,
    IExportPriceStore priceStore, IExportPriceSource prices, IOptionsMonitor<SolarSalesOptions> options,
    IOptionsMonitor<InverterConnectionOptions> devices, TimeProvider clock, ILogger<ExportSalesService> logger) : IExportSalesService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string Device, DateTimeOffset Start), DateTimeOffset> _historyAttempts = new();
    private DateTimeOffset _priceRetryAfter;
    private string? _lastPriceSource;
    private string? _lastFeedError;

    public async Task<ExportSalesResult> ReadAsync(ExportSalesRequest request, CancellationToken ct)
        => await ReadCoreAsync(request, ct, false);

    public async Task<ExportSalesResult> ReadDetailsAsync(ExportSalesRequest request, CancellationToken ct)
        => await ReadCoreAsync(request, ct, true);

    public async Task<ExportSalesResult> RecheckPricesAsync(ExportSalesRequest request, CancellationToken ct)
        => await ReadCoreAsync(request, ct, true, true);

    private async Task<ExportSalesResult> ReadCoreAsync(ExportSalesRequest request, CancellationToken ct, bool details, bool recheckPrices = false)
    {
        var now = clock.GetUtcNow();
        var config = options.CurrentValue;
        config.Validate();
        var pricing = ExportPriceConfiguration.Capture(config);
        var snapshot = (config.ContractStartDate, config.TimeZoneId, config.PayNegativePrices, pricing.Source, pricing.ManualPricePlnPerKwh, pricing.FeedUrl);
        var feedKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pricing.FeedUrl))).ToLowerInvariant();
        var priceIdentity = pricing.Source + ":" + feedKey + ":" + pricing.ManualPricePlnPerKwh.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (history is IInverterSelectionRefresher selection) await selection.RefreshSelectionAsync(ct);
        var sourceIdentity = InverterRefreshIdentity.Capture(devices.CurrentValue);
        var device = devices.CurrentValue.DeviceKey;
        var range = ExportSalesRange.Create(request, config, now);
        var utc = now.UtcDateTime;
        var currentStart = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        var includesCurrent = currentStart >= range.DataStart && currentStart >= range.Start
            && currentStart < range.End && now > currentStart;
        ExportSaleProgress? Current(IReadOnlyList<ExportGridSample> samples, IReadOnlyList<ExportPriceInterval> knownPrices)
        {
            if (!includesCurrent) return null;
            var through = samples.Where(sample => sample.Timestamp > currentStart && sample.Timestamp <= now)
                .Select(sample => (DateTimeOffset?)sample.Timestamp).Max();
            return through.HasValue
                ? ExportSalesCalculator.CalculateProgress(currentStart, through.Value,
                    samples.Where(sample => sample.Timestamp >= currentStart.AddMinutes(-10)
                        && sample.Timestamp <= through.Value).ToArray(), knownPrices, config.PayNegativePrices)
                : new(currentStart, null, null, null, null, null, 0);
        }
        ExportSalesResult Result(IReadOnlyList<ExportSaleHour> hours, string? dataError = null, string? priceError = null,
            ExportSaleProgress? current = null)
        {
            if (hours.Count == 0 && range.DataStart < range.DataEnd)
                hours = Calculate(range.DataStart, range.DataEnd, [], [], config.PayNegativePrices);
            var buckets = ExportSalesRange.Buckets(request, range.Start, range.End, config.TimeZoneId)
                .Select(bucket => ExportSalesCalculator.Aggregate(bucket.Start, bucket.End, hours)).ToArray();
            var total = ExportSalesCalculator.Aggregate(range.Start, range.End, hours);
            return new(request, range.Today, config.ContractStartDate, config.TimeZoneId, range.Start, range.End, buckets,
                total.ExportKwh, total.CreditedExportKwh, total.EnergyValuePln, total.EstimatedDepositPln,
                total.ExpectedHours, total.ObservedHours, total.ValuedHours, dataError, priceError,
                current ?? Current([], []), now)
            {
                PriceSource = pricing.Source,
                Hours = details ? hours : null,
                MissingPriceHours = details ? hours.Where(h => !h.MarketAveragePricePlnPerKwh.HasValue).Select(h => h.Start).ToArray() : null
            };
        }
        bool Changed() => !sourceIdentity.Matches(devices.CurrentValue) || snapshot !=
            (options.CurrentValue.ContractStartDate, options.CurrentValue.TimeZoneId, options.CurrentValue.PayNegativePrices, options.CurrentValue.PriceSource, options.CurrentValue.ManualPricePlnPerKwh, options.CurrentValue.PriceFeedUrl);
        if (range.DataEnd <= range.DataStart && !includesCurrent) return Result([]);
        if (string.IsNullOrWhiteSpace(device)) return Result([], "Select an inverter in Settings.");
        var from = range.DataStart.AddMinutes(-10);
        var through = includesCurrent ? now : range.DataEnd.AddMinutes(10) < now ? range.DataEnd.AddMinutes(10) : now;
        // SQL uses an exclusive end; admit an observation exactly at the captured time, never a future one.
        var readThrough = through == now ? through.AddTicks(1) : through;
        async Task<IReadOnlyList<ExportGridSample>> ReadSamples() =>
            (await readings.ReadAsync(device, from, readThrough, ct)).Where(sample => sample.Timestamp <= now).ToArray();
        await _gate.WaitAsync(ct);
        try
        {
            IReadOnlyList<ExportGridSample> samples;
            try { samples = await ReadSamples(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Sales readings unavailable ({ErrorType})", ex.GetType().Name);
                return Result([], "Inverter history is unavailable. Refresh the page to try again.");
            }
            var missing = Calculate(range.DataStart, range.DataEnd, samples, [], config.PayNegativePrices)
                .Where(hour => !hour.ExportKwh.HasValue).Select(hour => hour.Start).ToList();
            var initialCurrent = Current(samples, []);
            var refreshCurrent = initialCurrent is not null && (!initialCurrent.ExportKwh.HasValue
                || initialCurrent.ObservedThrough < now.AddMinutes(-10));
            if (refreshCurrent)
                missing.Add(currentStart);
            var windows = new List<(DateTimeOffset Start, DateTimeOffset End)>();
            for (var cursor = from; cursor < through; cursor = cursor.AddHours(6))
            {
                var end = cursor.AddHours(6) < through ? cursor.AddHours(6) : through;
                if (missing.Any(hour => hour < end && hour.AddHours(1) > cursor))
                    windows.Add((cursor, end));
            }
            // Keep live data fresh, then advance unseen history before retrying older incomplete batches.
            var pending = windows
                .OrderByDescending(window => refreshCurrent && window.Start <= currentStart && currentStart < window.End)
                .ThenBy(window => _historyAttempts.GetValueOrDefault((device, window.Start), DateTimeOffset.MinValue))
                .ThenBy(window => window.Start);
            string? dataError = null;
            var imported = false;
            var requests = 0;
            foreach (var window in pending)
            {
                ct.ThrowIfCancellationRequested();
                var key = (device, window.Start);
                if (_historyAttempts.TryGetValue(key, out var retryAt) && retryAt > now) continue;
                if (++requests > 14)
                {
                    dataError = "History is loading in batches. Refresh the page to load the next part.";
                    break;
                }
                try
                {
                    var fetched = await history.ReadAsync(device, window.Start, window.End, ct);
                    ct.ThrowIfCancellationRequested();
                    if (Changed()) return Result([], "Installation settings changed. Refresh the page.");
                    await readings.UpsertHistoryAsync(device, fetched, clock.GetUtcNow(), ct);
                    imported = true;
                    _historyAttempts[key] = now.AddMinutes(5);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Inverter sales backfill unavailable ({ErrorType})", ex.GetType().Name);
                    _historyAttempts[key] = now.AddMinutes(2);
                    dataError = "Some Inverter history is unavailable. Totals include only complete hours with data.";
                }
            }
            if (imported)
            {
                try { samples = await ReadSamples(); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Imported sales readings unavailable ({ErrorType})", ex.GetType().Name);
                    return Result([], "History was saved but is temporarily unavailable. Refresh the page to try again.");
                }
            }
            ct.ThrowIfCancellationRequested();
            if (Changed()) return Result([], "Installation settings changed. Refresh the page.");

            if (_lastPriceSource != priceIdentity) { _lastPriceSource = priceIdentity; _priceRetryAfter = default; _lastFeedError = null; }
            IReadOnlyList<ExportPriceInterval> storedPrices;
            string? priceError = pricing.Source == "feed" ? _lastFeedError : null;
            var priceEnd = includesCurrent ? currentStart.AddHours(1) : range.DataEnd;
            try { storedPrices = pricing.Source switch
            {
                "manual" => ConfiguredExportPriceSource.Manual(pricing.ManualPricePlnPerKwh, range.DataStart, priceEnd),
                "feed" => await ((IScopedExportPriceStore)priceStore).ReadFeedAsync(feedKey, range.DataStart, priceEnd, ct),
                _ => await priceStore.ReadAsync(range.DataStart, priceEnd, ct)
            }; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Sales price storage unavailable ({ErrorType})", ex.GetType().Name);
                storedPrices = [];
                priceError = "Stored prices are unavailable. The total value includes only hours with known prices.";
            }
            var hours = Calculate(range.DataStart, range.DataEnd, samples, storedPrices, config.PayNegativePrices);
            var current = Current(samples, storedPrices);
            var unpriced = hours.Where(hour => recheckPrices ? !hour.MarketAveragePricePlnPerKwh.HasValue : hour.CreditedExportKwh > 0 && !hour.EnergyValuePln.HasValue)
                .Select(hour => hour.Start).ToList();
            if (current is { CreditedExportKwh: > 0, EnergyValuePln: null }) unpriced.Add(current.Start);
            if ((unpriced.Count > 0 || pricing.Source == "feed") && (recheckPrices || _priceRetryAfter <= now))
            {
                try
                {
                    var fetchStart = pricing.Source == "feed" ? range.DataStart : unpriced[0];
                    var fetchEnd = pricing.Source == "feed" ? priceEnd : unpriced[^1].AddHours(1);
                    var fetched = prices is IConfiguredExportPriceSource configured
                        ? await configured.ReadAsync(pricing, fetchStart, fetchEnd, ct)
                        : await prices.ReadAsync(fetchStart, fetchEnd, ct);
                    ct.ThrowIfCancellationRequested();
                    if (Changed()) return Result([], "Installation settings changed. Refresh the page.");
                    foreach (var batch in fetched.Chunk(1000))
                    {
                        if (pricing.Source == "feed") await ((IScopedExportPriceStore)priceStore).SaveFeedAsync(feedKey, batch, clock.GetUtcNow(), ct);
                        else if (pricing.Source == "pse") await priceStore.SaveAsync(batch, clock.GetUtcNow(), ct);
                    }
                    // A successful but partial publication must not erase previously trusted prices.
                    storedPrices = storedPrices.Concat(fetched).GroupBy(price => price.Start).Select(group => group.Last()).ToArray();
                    _priceRetryAfter = now.AddMinutes(2);
                    priceError = null; _lastFeedError = null;
                    hours = Calculate(range.DataStart, range.DataEnd, samples, storedPrices, config.PayNegativePrices);
                    current = Current(samples, storedPrices);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Export sales prices unavailable ({ErrorType})", ex.GetType().Name);
                    _priceRetryAfter = now.AddMinutes(2);
                    priceError = pricing.Source == "pse" ? "PSE prices are temporarily unavailable. Stored prices are preserved; missing intervals have no value estimate." : "The price feed is temporarily unavailable. Saved feed prices are preserved; missing intervals have no value estimate.";
                    if (pricing.Source == "feed") _lastFeedError = priceError;
                }
            }
            ct.ThrowIfCancellationRequested();
            if (Changed()) return Result([], "Installation settings changed. Refresh the page.");
            if (hours.Any(hour => !hour.ExportKwh.HasValue) && dataError is null)
                dataError = "There are gaps in the measurements. Totals include only complete hours with data.";
            if (hours.Any(hour => hour.ExportKwh.HasValue && !hour.EnergyValuePln.HasValue) && priceError is null)
                priceError = "Prices are not published for every hour. The total value includes only hours with available prices.";
            // Attempts are only a short retry throttle; long-term history lives in SQL.
            foreach (var key in _historyAttempts.Where(entry => entry.Value < now.AddDays(-1)).Select(entry => entry.Key).ToArray())
                _historyAttempts.Remove(key);
            return Result(hours, dataError, priceError, current);
        }
        finally { _gate.Release(); }
    }

    private static List<ExportSaleHour> Calculate(DateTimeOffset start, DateTimeOffset end,
        IReadOnlyList<ExportGridSample> samples, IReadOnlyList<ExportPriceInterval> prices, bool payNegativePrices)
    {
        DateTimeOffset Hour(DateTimeOffset time)
        {
            var utc = time.UtcDateTime;
            return new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        }
        var sampleHours = samples.GroupBy(sample => Hour(sample.Timestamp)).ToDictionary(group => group.Key, group => group.ToArray());
        var priceHours = prices.GroupBy(price => Hour(price.Start)).ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<ExportSaleHour>();
        for (var hour = start; hour < end; hour = hour.AddHours(1))
        {
            var around = new[] { hour.AddHours(-1), hour, hour.AddHours(1) }
                .SelectMany(key => sampleHours.TryGetValue(key, out var values) ? values : []).ToArray();
            var hourPrices = priceHours.TryGetValue(hour, out var known) ? known.OrderBy(p => p.Start).ToArray() : [];
            var complete = hourPrices.Length == 4 && hourPrices.Select((price, index) =>
                price.Start == hour.AddMinutes(index * 15) && price.End == price.Start.AddMinutes(15)).All(valid => valid);
            result.Add(ExportSalesCalculator.CalculateHour(hour, around, hourPrices, payNegativePrices) with
            {
                MarketAveragePricePlnPerKwh = complete ? hourPrices.Average(p => p.PricePlnPerMwh) / 1000m : null
            });
        }
        return result;
    }
}

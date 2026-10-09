using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Infrastructure.Settlement;

public sealed record ExportPriceConfiguration(string Source, decimal ManualPricePlnPerKwh, string FeedUrl)
{
    public static ExportPriceConfiguration Capture(SolarSalesOptions value) => new(value.PriceSource, value.ManualPricePlnPerKwh, value.PriceFeedUrl);
}
public interface IConfiguredExportPriceSource : IExportPriceSource
{
    Task<IReadOnlyList<ExportPriceInterval>> ReadAsync(ExportPriceConfiguration configuration, DateTimeOffset start, DateTimeOffset end, CancellationToken ct);
}
public sealed class ConfiguredExportPriceSource(PseExportPriceClient pse, ExportPriceFeedClient feed,
    IOptionsMonitor<SolarSalesOptions> options) : IConfiguredExportPriceSource
{
    public Task<IReadOnlyList<ExportPriceInterval>> ReadAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        => ReadAsync(ExportPriceConfiguration.Capture(options.CurrentValue), start, end, ct);
    public Task<IReadOnlyList<ExportPriceInterval>> ReadAsync(ExportPriceConfiguration config, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        new SolarSalesOptions { PriceSource = config.Source, ManualPricePlnPerKwh = config.ManualPricePlnPerKwh, PriceFeedUrl = config.FeedUrl }.Validate();
        return config.Source switch
        {
            "manual" => Task.FromResult(Manual(config.ManualPricePlnPerKwh, start, end)),
            "feed" => feed.ReadAsync(config.FeedUrl, start, end, ct),
            _ => pse.ReadAsync(start, end, ct)
        };
    }
    public static IReadOnlyList<ExportPriceInterval> Manual(decimal pricePlnPerKwh, DateTimeOffset start, DateTimeOffset end)
    {
        if (pricePlnPerKwh is < 0 or > 1000 || decimal.Round(pricePlnPerKwh, 6) != pricePlnPerKwh
            || end <= start || end - start > TimeSpan.FromDays(367) || start.UtcTicks % TimeSpan.FromMinutes(15).Ticks != 0
            || end.UtcTicks % TimeSpan.FromMinutes(15).Ticks != 0) throw new ArgumentException("Invalid manual price interval.");
        var result = new List<ExportPriceInterval>();
        for (var time = start.ToUniversalTime(); time < end; time = time.AddMinutes(15))
            result.Add(new(time, time.AddMinutes(15), pricePlnPerKwh * 1000m));
        return result;
    }
}

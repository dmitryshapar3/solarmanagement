using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Services;

public static class ExportSalesCalculator
{
    public const decimal DepositMultiplier = 1.23m;
    private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromMinutes(10);

    public static ExportSaleHour CalculateHour(DateTimeOffset start, IReadOnlyList<ExportGridSample> samples,
        IReadOnlyList<ExportPriceInterval> prices, bool payNegativePrices = false)
    {
        start = HourStart(start);
        var energy = Integrate(start, start.AddHours(1), samples);
        var value = ValueEnergy(start, energy.CreditedExportKwh, prices, payNegativePrices);
        return new(start, energy.ExportKwh, energy.ImportKwh, energy.CreditedExportKwh,
            value.EnergyValuePln, energy.ObservedSeconds, value.AveragePricePlnPerKwh);
    }

    public static ExportSaleProgress CalculateProgress(DateTimeOffset start, DateTimeOffset through,
        IReadOnlyList<ExportGridSample> samples, IReadOnlyList<ExportPriceInterval> prices,
        bool payNegativePrices = false)
    {
        start = HourStart(start);
        through = through.ToUniversalTime();
        if (through <= start || through >= start.AddHours(1))
            throw new ArgumentException("Sales progress must end inside its UTC hour.", nameof(through));
        // Later readings cannot extend or interpolate a provisional observation into the future.
        var energy = Integrate(start, through, samples.Where(sample => sample.Timestamp <= through));
        var value = ValueEnergy(start, energy.CreditedExportKwh, prices, payNegativePrices);
        return new(start, energy.ExportKwh.HasValue ? through : null, energy.ExportKwh,
            energy.CreditedExportKwh, value.EnergyValuePln, EstimateDeposit(value.EnergyValuePln), energy.ObservedSeconds);
    }

    private static IntegratedEnergy Integrate(DateTimeOffset start, DateTimeOffset end,
        IEnumerable<ExportGridSample> samples)
    {
        var ordered = samples.OrderBy(sample => sample.Timestamp).ToArray();
        decimal exportArea = 0, importArea = 0;
        long coveredTicks = 0;
        for (var i = 1; i < ordered.Length; i++)
        {
            var left = ordered[i - 1];
            var right = ordered[i];
            var gap = right.Timestamp - left.Timestamp;
            if (gap <= TimeSpan.Zero)
                throw new ArgumentException("Grid samples must have unique measurement timestamps.", nameof(samples));
            if (gap > MaximumSampleGap || right.Timestamp <= start || left.Timestamp >= end) continue;
            var from = left.Timestamp > start ? left.Timestamp : start;
            var through = right.Timestamp < end ? right.Timestamp : end;
            if (through <= from) continue;
            var first = Interpolate(left.GridPowerWatts, right.GridPowerWatts,
                (decimal)(from - left.Timestamp).Ticks / gap.Ticks);
            var last = Interpolate(left.GridPowerWatts, right.GridPowerWatts,
                (decimal)(through - left.Timestamp).Ticks / gap.Ticks);
            var ticks = (through - from).Ticks;
            // Split a sign crossing at its actual interpolated zero instead of clipping both endpoints.
            importArea += PositiveArea(first, last, ticks);
            exportArea += PositiveArea(-first, -last, ticks);
            coveredTicks += ticks;
        }

        var seconds = (int)(coveredTicks / TimeSpan.TicksPerSecond);
        // Gaps cannot be assigned to a tariff interval or certified as zero export.
        if (coveredTicks != (end - start).Ticks)
            return new(null, null, null, seconds);
        // Convert the accumulated watt-ticks once, avoiding recurring decimal hours per sample.
        var export = exportArea / (TimeSpan.TicksPerHour * 1000m);
        var import = importArea / (TimeSpan.TicksPerHour * 1000m);
        return new(export, import, Math.Max(0, export - import), seconds);
    }

    private static (decimal? EnergyValuePln, decimal? AveragePricePlnPerKwh) ValueEnergy(DateTimeOffset start,
        decimal? credited, IReadOnlyList<ExportPriceInterval> prices, bool payNegativePrices)
    {
        if (!credited.HasValue) return (null, null);
        var end = start.AddHours(1);
        var hourlyPrices = prices.Where(price => price.Start >= start && price.Start < end)
            .OrderBy(price => price.Start).ToArray();
        var completePrices = hourlyPrices.Length == 4 && hourlyPrices.Select((price, index) =>
            price.Start == start.AddMinutes(index * 15) && price.End == price.Start.AddMinutes(15)).All(valid => valid);
        if (!completePrices)
            return (credited == 0 ? 0 : null, null);
        // TAURON annex section4: hourly net export is split equally; floor each imbalance price first.
        var pricePerKwh = hourlyPrices.Sum(price => payNegativePrices ? price.PricePlnPerMwh
            : Math.Max(0, price.PricePlnPerMwh)) / 4m / 1000m;
        return (credited * pricePerKwh, pricePerKwh);
    }

    public static ExportSaleBucket Aggregate(DateTimeOffset start, DateTimeOffset end,
        IReadOnlyList<ExportSaleHour> hours)
    {
        var selected = hours.Where(hour => hour.Start >= start && hour.Start < end).ToArray();
        var observed = selected.Where(hour => hour.ExportKwh.HasValue).ToArray();
        var valued = selected.Where(hour => hour.EnergyValuePln.HasValue).ToArray();
        decimal? export = observed.Length == 0 ? null : observed.Sum(hour => hour.ExportKwh!.Value);
        decimal? credited = observed.Length == 0 ? null : observed.Sum(hour => hour.CreditedExportKwh!.Value);
        decimal? value = valued.Length == 0 ? null : valued.Sum(hour => hour.EnergyValuePln!.Value);
        // Preserve full precision through aggregation; round only when presenting currency.
        return new(start, end, export, credited, value, EstimateDeposit(value),
            selected.Length, observed.Length, valued.Length);
    }

    private static DateTimeOffset HourStart(DateTimeOffset start)
    {
        start = start.ToUniversalTime();
        if (start.Minute != 0 || start.Second != 0 || start.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentException("Sales hours must start on a UTC hour boundary.", nameof(start));
        return start;
    }

    private sealed record IntegratedEnergy(decimal? ExportKwh, decimal? ImportKwh,
        decimal? CreditedExportKwh, int ObservedSeconds);

    private static decimal? EstimateDeposit(decimal? energyValue) => energyValue * DepositMultiplier;

    private static decimal Interpolate(decimal from, decimal through, decimal fraction) => from + (through - from) * fraction;

    private static decimal PositiveArea(decimal first, decimal last, decimal duration)
    {
        if (first >= 0 && last >= 0) return (first + last) / 2m * duration;
        if (first <= 0 && last <= 0) return 0;
        var positive = Math.Max(first, last);
        return positive / 2m * duration * positive / Math.Abs(last - first);
    }
}

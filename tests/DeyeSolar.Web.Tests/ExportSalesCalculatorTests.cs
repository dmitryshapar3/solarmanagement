using System.Globalization;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Services;

namespace DeyeSolar.Web.Tests;

public class ExportSalesCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HourlyNettingAndQuarterPriceFloorPrecedeDepositMultiplication()
    {
        var samples = Enumerable.Range(0, 7).Select(i => new ExportGridSample(Start.AddMinutes(i * 10), -2000)).ToArray();
        var prices = Prices(-100m, 100m, 300m, 500m);
        var hour = ExportSalesCalculator.CalculateHour(Start, samples, prices);
        Assert.Equal(2m, hour.ExportKwh);
        Assert.Equal(0m, hour.ImportKwh);
        Assert.Equal(2m, hour.CreditedExportKwh);
        Assert.Equal(0.225m, hour.AveragePricePlnPerKwh);
        Assert.Equal(0.45m, hour.EnergyValuePln);
        var total = ExportSalesCalculator.Aggregate(Start, Start.AddHours(1), [hour]);
        Assert.Equal(0.5535m, total.EstimatedDepositPln);
        Assert.Equal(0.4m, ExportSalesCalculator.CalculateHour(Start, samples, prices, payNegativePrices: true).EnergyValuePln);
    }

    [Fact]
    public void ExplicitNegativePricePolicyPreservesNegativeValueAndDeposit()
    {
        var samples = Enumerable.Range(0, 7).Select(i => new ExportGridSample(Start.AddMinutes(i * 10), -1000)).ToArray();
        var prices = Prices(-500, -500, -500, -500);
        Assert.Equal(0m, ExportSalesCalculator.CalculateHour(Start, samples, prices).EnergyValuePln);
        var hour = ExportSalesCalculator.CalculateHour(Start, samples, prices, payNegativePrices: true);
        Assert.Equal(-0.5m, hour.EnergyValuePln);
        Assert.Equal(-0.615m, ExportSalesCalculator.Aggregate(Start, Start.AddHours(1), [hour]).EstimatedDepositPln);
    }

    [Fact]
    public void SignCrossingUsesTwoTrianglesAndBalancesImportBeforeValuation()
    {
        var samples = Enumerable.Range(0, 7).Select(i => new ExportGridSample(Start.AddMinutes(i * 10), i % 2 == 0 ? -3000 : 1000)).ToArray();
        var hour = ExportSalesCalculator.CalculateHour(Start, samples, Prices(500, 500, 500, 500));
        // Six ten-minute triangles: export height3000/base7.5min; import height1000/base2.5min.
        Assert.Equal(1.125m, hour.ExportKwh);
        Assert.Equal(0.125m, hour.ImportKwh);
        Assert.Equal(1m, hour.CreditedExportKwh);
        Assert.Equal(0.5m, hour.EnergyValuePln);
    }

    [Fact]
    public void BoundaryNeighborsAreClippedAndOtherHoursDoNotContribute()
    {
        var samples = Enumerable.Range(-1, 15).Select(i => new ExportGridSample(Start.AddMinutes(i * 5 + 2), -1000)).ToArray();
        var hour = ExportSalesCalculator.CalculateHour(Start, samples, Prices(123.456789m, 123.456789m, 123.456789m, 123.456789m));
        Assert.Equal(1m, hour.ExportKwh);
        Assert.Equal(3600, hour.ObservedSeconds);
        Assert.Equal(0.123456789m, hour.EnergyValuePln);
        Assert.Equal(0.15185185047m, ExportSalesCalculator.Aggregate(Start, Start.AddHours(1), [hour]).EstimatedDepositPln);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(0)]
    [InlineData(60)]
    public void MissingOrTooWideIntervalsAreUnknownEvenWhenOtherSamplesAreZero(int removedMinute)
    {
        var samples = Enumerable.Range(0, 7).Where(i => i * 10 != removedMinute)
            .Select(i => new ExportGridSample(Start.AddMinutes(i * 10), 0)).ToArray();
        var hour = ExportSalesCalculator.CalculateHour(Start, samples, Prices(500, 500, 500, 500));
        Assert.Null(hour.ExportKwh);
        Assert.Null(hour.CreditedExportKwh);
        Assert.Null(hour.EnergyValuePln);
        Assert.InRange(hour.ObservedSeconds, 0, 3599);
    }

    [Fact]
    public void MissingPricesNeverBecomeZeroButFullyObservedImportNeedsNoPrice()
    {
        ExportGridSample[] Samples(int watts) => Enumerable.Range(0, 7).Select(i => new ExportGridSample(Start.AddMinutes(i * 10), watts)).ToArray();
        var exported = ExportSalesCalculator.CalculateHour(Start, Samples(-1000), Prices(100, 200, 300, 400).Take(3).ToArray());
        Assert.Equal(1m, exported.ExportKwh);
        Assert.Null(exported.EnergyValuePln);
        var imported = ExportSalesCalculator.CalculateHour(Start, Samples(1000), []);
        Assert.Equal(1m, imported.ImportKwh);
        Assert.Equal(0m, imported.CreditedExportKwh);
        Assert.Equal(0m, imported.EnergyValuePln);
        Assert.Null(imported.AveragePricePlnPerKwh);
    }

    [Fact]
    public void DuplicateOrMalformedIntervalsCannotCertifyAnHour()
    {
        var samples = Enumerable.Range(0, 7).Select(i => new ExportGridSample(Start.AddMinutes(i * 10), -1000)).ToArray();
        Assert.Throws<ArgumentException>(() => ExportSalesCalculator.CalculateHour(Start, samples.Append(samples[0]).ToArray(), []));
        Assert.Throws<ArgumentException>(() => ExportSalesCalculator.CalculateHour(Start.AddSeconds(1), samples, []));
        var prices = Prices(1, 2, 3, 4);
        Assert.Null(ExportSalesCalculator.CalculateHour(Start, samples, [prices[0], prices[1], prices[1], prices[3]]).EnergyValuePln);
        Assert.Null(ExportSalesCalculator.CalculateHour(Start, samples, [prices[0] with { End = Start.AddMinutes(30) }, prices[1], prices[2], prices[3]]).EnergyValuePln);
    }

    [Fact]
    public void PartialAggregationRetainsUnknownCountsPrecisionAndHalfOpenBoundaries()
    {
        ExportSaleHour[] hours = [new(Start.AddHours(-1), 900, 0, 900, 900, 3600, 1),
            new(Start, 1, 0, 1, 0.123456m, 3600, 0.123456m),
            new(Start.AddHours(1), 2, 0, 2, null, 3600, null),
            new(Start.AddHours(2), null, null, null, null, 600, null),
            new(Start.AddHours(3), 900, 0, 900, 900, 3600, 1)];
        var total = ExportSalesCalculator.Aggregate(Start, Start.AddHours(3), hours);
        Assert.Equal(3m, total.ExportKwh);
        Assert.Equal(0.123456m, total.EnergyValuePln);
        Assert.Equal(0.15185088m, total.EstimatedDepositPln);
        Assert.Equal((3, 2, 1), (total.ExpectedHours, total.ObservedHours, total.ValuedHours));
        Assert.Null(ExportSalesCalculator.Aggregate(Start.AddHours(2), Start.AddHours(3), hours).ExportKwh);
        Assert.Null(ExportSalesCalculator.Aggregate(Start.AddDays(2), Start.AddDays(3), hours).EnergyValuePln);
    }

    [Theory]
    [InlineData(25, "2.5", "0.5625", "0.691875")]
    [InlineData(30, "3", "0.675", "0.83025")]
    [InlineData(55, "5.5", "1.2375", "1.522125")]
    public void ProgressValuesOnlyTheMeasuredPartUsingEveryQuarterOfTheHour(int minutes,
        string exported, string value, string deposit)
    {
        var through = Start.AddMinutes(minutes);
        var samples = Enumerable.Range(0, minutes / 5 + 1)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), -6000)).ToArray();

        var progress = ExportSalesCalculator.CalculateProgress(Start, through, samples, Prices(-100, 100, 300, 500));

        Assert.Equal(Start, progress.Start);
        Assert.Equal(through, progress.ObservedThrough);
        Assert.Equal(decimal.Parse(exported, CultureInfo.InvariantCulture), progress.ExportKwh);
        Assert.Equal(progress.ExportKwh, progress.CreditedExportKwh);
        Assert.Equal(decimal.Parse(value, CultureInfo.InvariantCulture), progress.EnergyValuePln);
        Assert.Equal(decimal.Parse(deposit, CultureInfo.InvariantCulture), progress.EstimatedDepositPln);
        Assert.Equal(minutes * 60, progress.ObservedSeconds);
    }

    [Fact]
    public void ProgressSplitsSignCrossingsAndNetsImportBeforeValuation()
    {
        var samples = Enumerable.Range(0, 4)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 10), index % 2 == 0 ? -3000 : 1000)).ToArray();

        var progress = ExportSalesCalculator.CalculateProgress(Start, Start.AddMinutes(30), samples, Prices(500, 500, 500, 500));

        // Three ten-minute segments contain 0.5625 kWh export and 0.0625 kWh import.
        Assert.Equal(0.5625m, progress.ExportKwh);
        Assert.Equal(0.5m, progress.CreditedExportKwh);
        Assert.Equal(0.25m, progress.EnergyValuePln);
        Assert.Equal(0.3075m, progress.EstimatedDepositPln);
        Assert.Equal(1800, progress.ObservedSeconds);
    }

    [Fact]
    public void ProgressRequiresFutureQuarterPricesButKnownZeroNetNeedsNoPrice()
    {
        ExportGridSample[] Samples(int watts) => Enumerable.Range(0, 6)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), watts)).ToArray();
        var through = Start.AddMinutes(25);
        var exported = ExportSalesCalculator.CalculateProgress(Start, through, Samples(-6000), Prices(500, 500));
        Assert.Equal(through, exported.ObservedThrough);
        Assert.Equal(2.5m, exported.ExportKwh);
        Assert.Equal(2.5m, exported.CreditedExportKwh);
        Assert.Null(exported.EnergyValuePln);
        Assert.Null(exported.EstimatedDepositPln);

        foreach (var watts in new[] { 0, 1000 })
        {
            var zero = ExportSalesCalculator.CalculateProgress(Start, through, Samples(watts), []);
            Assert.Equal(through, zero.ObservedThrough);
            Assert.Equal(0m, zero.ExportKwh);
            Assert.Equal(0m, zero.CreditedExportKwh);
            Assert.Equal(0m, zero.EnergyValuePln);
            Assert.Equal(0m, zero.EstimatedDepositPln);
        }
    }

    [Fact]
    public void ProgressPreservesTheExplicitNegativePricePolicy()
    {
        var samples = Enumerable.Range(0, 7)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), -2000)).ToArray();
        var prices = Prices(-500, -500, -500, -500);
        var floored = ExportSalesCalculator.CalculateProgress(Start, Start.AddMinutes(30), samples, prices);
        var signed = ExportSalesCalculator.CalculateProgress(Start, Start.AddMinutes(30), samples, prices, payNegativePrices: true);

        Assert.Equal(1m, signed.ExportKwh);
        Assert.Equal(0m, floored.EnergyValuePln);
        Assert.Equal(0m, floored.EstimatedDepositPln);
        Assert.Equal(-0.5m, signed.EnergyValuePln);
        Assert.Equal(-0.615m, signed.EstimatedDepositPln);
    }

    [Fact]
    public void ProgressClipsThePreviousHourAndIgnoresLaterReadingsAndNeighborPrices()
    {
        var through = Start.AddMinutes(25);
        var samples = Enumerable.Range(-1, 6)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5 + 2), -6000)).ToList();
        samples.Add(new(through, -6000));
        samples.Add(new(through.AddMinutes(1), 200000));
        samples.Add(new(Start.AddHours(1), 200000));
        var prices = Prices(400, 400, 400, 400)
            .Append(new(Start.AddMinutes(-15), Start, 99999m))
            .Append(new(Start.AddHours(1), Start.AddMinutes(75), 99999m)).ToArray();

        var progress = ExportSalesCalculator.CalculateProgress(Start, through, samples, prices);

        Assert.Equal(through, progress.ObservedThrough);
        Assert.Equal(1500, progress.ObservedSeconds);
        Assert.Equal(2.5m, progress.ExportKwh);
        Assert.Equal(2.5m, progress.CreditedExportKwh);
        Assert.Equal(1m, progress.EnergyValuePln);
        Assert.Equal(1.23m, progress.EstimatedDepositPln);
    }

    [Theory]
    [InlineData("empty", 0)]
    [InlineData("missing-start", 1200)]
    [InlineData("wide-gap", 600)]
    [InlineData("future-endpoint", 1200)]
    public void IncompleteProgressNeverBecomesKnownZeroOrExtrapolates(string scenario, int observedSeconds)
    {
        var through = Start.AddMinutes(25);
        int[] minutes = scenario switch
        {
            "empty" => [],
            "missing-start" => [5, 10, 15, 20, 25],
            "wide-gap" => [0, 5, 20, 25],
            "future-endpoint" => [0, 5, 10, 15, 20, 30],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var samples = minutes.Select(minute => new ExportGridSample(Start.AddMinutes(minute), 0)).ToArray();

        var progress = ExportSalesCalculator.CalculateProgress(Start, through, samples, Prices(500, 500, 500, 500));

        Assert.Null(progress.ObservedThrough);
        Assert.Null(progress.ExportKwh);
        Assert.Null(progress.CreditedExportKwh);
        Assert.Null(progress.EnergyValuePln);
        Assert.Null(progress.EstimatedDepositPln);
        Assert.Equal(observedSeconds, progress.ObservedSeconds);
    }

    [Fact]
    public void SubsecondProgressPreservesWattTicksAndDepositPrecision()
    {
        var through = Start.AddMinutes(25).AddTicks(1234567);
        var samples = Enumerable.Range(0, 6)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), -3600))
            .Append(new(through, -3600)).ToArray();

        var progress = ExportSalesCalculator.CalculateProgress(Start, through, samples, Prices(123.456789m, 123.456789m, 123.456789m, 123.456789m));

        Assert.Equal(through, progress.ObservedThrough);
        Assert.Equal(1.5001234567m, progress.ExportKwh);
        Assert.Equal(0.1852004250677625363m, progress.EnergyValuePln);
        Assert.Equal(0.227796522833347919649m, progress.EstimatedDepositPln);
        Assert.Equal(1500, progress.ObservedSeconds);
    }

    [Fact]
    public void ProgressRequiresCoverageOfEveryTickEvenWhenWholeSecondCountsMatch()
    {
        var samples = Enumerable.Range(0, 6)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), -3600)).ToArray();

        var progress = ExportSalesCalculator.CalculateProgress(Start, Start.AddMinutes(25).AddTicks(1), samples, Prices(500, 500, 500, 500));

        Assert.Equal(1500, progress.ObservedSeconds);
        Assert.Null(progress.ObservedThrough);
        Assert.Null(progress.ExportKwh);
        Assert.Null(progress.CreditedExportKwh);
        Assert.Null(progress.EnergyValuePln);
        Assert.Null(progress.EstimatedDepositPln);
    }

    [Theory]
    [InlineData("2026-09-28T00:00:00+02:00", "2026-09-27T22:00:00+00:00")]
    [InlineData("2026-03-29T03:00:00+02:00", "2026-03-29T01:00:00+00:00")]
    [InlineData("2026-10-25T02:00:00+02:00", "2026-10-25T00:00:00+00:00")]
    [InlineData("2026-10-25T02:00:00+01:00", "2026-10-25T01:00:00+00:00")]
    public void ProgressPreservesDistinctUtcHoursAtLocalMidnightAndDst(string localStart, string utcStart)
    {
        var start = DateTimeOffset.Parse(localStart, CultureInfo.InvariantCulture);
        var expected = DateTimeOffset.Parse(utcStart, CultureInfo.InvariantCulture);
        var samples = Enumerable.Range(0, 7).Select(index => new ExportGridSample(start.AddMinutes(index * 5), -2000)).ToArray();
        var prices = Enumerable.Range(0, 4)
            .Select(index => new ExportPriceInterval(expected.AddMinutes(index * 15), expected.AddMinutes((index + 1) * 15), 500)).ToArray();

        var progress = ExportSalesCalculator.CalculateProgress(start, start.AddMinutes(30), samples, prices);

        Assert.Equal(expected, progress.Start);
        Assert.Equal(TimeSpan.Zero, progress.Start.Offset);
        Assert.Equal(expected.AddMinutes(30), progress.ObservedThrough);
        Assert.Equal(TimeSpan.Zero, progress.ObservedThrough!.Value.Offset);
        Assert.Equal(1m, progress.ExportKwh);
        Assert.Equal(0.5m, progress.EnergyValuePln);
        Assert.Equal(0.615m, progress.EstimatedDepositPln);
    }

    [Fact]
    public void ProgressCanBeRevisedByLaterImportWithoutChangingCompletedHourRules()
    {
        var samples = Enumerable.Range(0, 13)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), index <= 5 ? -6000 : 6000)).ToArray();

        var progress = ExportSalesCalculator.CalculateProgress(Start, Start.AddMinutes(25), samples, Prices(500, 500, 500, 500));
        var completed = ExportSalesCalculator.CalculateHour(Start, samples, Prices(500, 500, 500, 500));

        Assert.Equal(2.5m, progress.CreditedExportKwh);
        Assert.Equal(1.25m, progress.EnergyValuePln);
        Assert.Equal(0m, completed.CreditedExportKwh);
        Assert.Equal(0m, completed.EnergyValuePln);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(61)]
    public void ProgressRejectsThroughOutsideTheOpenHour(int minute)
    {
        Assert.Throws<ArgumentException>(() => ExportSalesCalculator.CalculateProgress(Start, Start.AddMinutes(minute), [], []));
    }

    [Fact]
    public void ProgressRejectsUnalignedHourAndDuplicateMeasuredTimestamps()
    {
        Assert.Throws<ArgumentException>(() => ExportSalesCalculator.CalculateProgress(Start.AddTicks(1), Start.AddMinutes(25), [], []));
        ExportGridSample[] duplicates = [new(Start, -1000), new(Start.AddMinutes(5), -1000), new(Start.AddMinutes(5), -1000)];
        Assert.Throws<ArgumentException>(() => ExportSalesCalculator.CalculateProgress(Start, Start.AddMinutes(25), duplicates, []));
    }

    private static ExportPriceInterval[] Prices(params decimal[] prices) => prices.Select((price, i) =>
        new ExportPriceInterval(Start.AddMinutes(i * 15), Start.AddMinutes((i + 1) * 15), price)).ToArray();
}

using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tests;

public class SolarHistoryServiceTests
{
    private static readonly DateTimeOffset Hour = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("weather", "Weather history is unavailable. Try refreshing the chart later.")]
    [InlineData("actual", "Inverter history is unavailable. Try refreshing the chart later.")]
    [InlineData("unconfirmed", "Confirm the selected inverter's PV power type before comparing readings.")]
    [InlineData("changed", "Installation settings changed. Refresh the chart.")]
    public async Task EveryGenerationSourceFailureHasAnEnglishMessage(string scenario, string expected)
    {
        var fixture = new Fixture();
        if (scenario == "weather") fixture.Weather.Fail = true;
        if (scenario == "actual") fixture.Store.Fail = true;
        if (scenario == "unconfirmed") fixture.Config.CurrentValue.DeyeSolarPowerIsPvDcConfirmed = false;
        if (scenario == "changed") fixture.Store.OnRead = () => fixture.Deye.CurrentValue = new() { DeviceSn = "replacement" };
        using var service = fixture.Service();

        var result = await service.ReadAsync(SolarHistoryPeriod.Today, default);

        Assert.Equal(expected, scenario == "weather" ? result.WeatherError : result.ActualError);
        foreach (var message in new[] { result.WeatherError, result.ActualError }.OfType<string>())
            Assert.DoesNotMatch("[\\u0400-\\u04FF]", message);
        if (scenario == "unconfirmed") Assert.Equal(0, fixture.Store.Calls);
        if (scenario == "changed") Assert.Empty(result.Points);
    }

    [Theory]
    [InlineData(2026, 3, 29, 21, 22)]
    [InlineData(2026, 10, 25, 22, 24)]
    [InlineData(2026, 9, 29, 22, 0)]
    public void CalendarDayUsesWarsawMidnightAndOnlyCompletedUtcHours(int year, int month, int day, int utcHour, int hours)
    {
        var now = new DateTimeOffset(year, month, day, utcHour, 37, 0, TimeSpan.Zero);
        var range = SolarHistoryAggregation.Range(SolarHistoryPeriod.Today, now, "Europe/Warsaw");
        Assert.Equal(hours, (range.End - range.Start).TotalHours);
        Assert.Equal(0, range.End.Minute);
        Assert.True(range.End <= now);
    }

    [Fact]
    public void AutumnChangeRetainsBothOccurrencesOfTheRepeatedHour()
    {
        var range = SolarHistoryAggregation.Range(SolarHistoryPeriod.Today,
            new(2026, 10, 25, 22, 59, 0, TimeSpan.Zero), "Europe/Warsaw");
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero), range.Start);
        Assert.Equal(24, (range.End - range.Start).TotalHours);
    }

    [Theory]
    [InlineData(SolarHistoryPeriod.Week, 7)]
    [InlineData(SolarHistoryPeriod.Month, 30)]
    public void LongerRangesIncludeTodayAndPreviousLocalCalendarDays(SolarHistoryPeriod period, int days)
    {
        var range = SolarHistoryAggregation.Range(period, Hour.AddMinutes(37), "Europe/Warsaw");
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 22, 0, 0, TimeSpan.Zero).AddDays(1 - days), range.Start);
        Assert.Equal(Hour, range.End);
    }

    [Fact]
    public void MeanIsTimeWeightedAndUnchangedByPollDuplicates()
    {
        // Linear rise from 0 to 6 kW over one hour integrates to exactly 3 kWh.
        var samples = Enumerable.Range(0, 13).Select(i => new SolarActual(Hour.AddMinutes(i * 5), i * .5, SolarPowerBasis.PvDc)).ToArray();
        Assert.Equal(3, SolarHistoryAggregation.MeanPower(samples, Hour));
        var duplicates = samples.Concat(Enumerable.Repeat(samples[1], 80)).OrderBy(s => s.Timestamp).ToArray();
        Assert.Equal(3, SolarHistoryAggregation.MeanPower(duplicates, Hour));
    }

    [Fact]
    public void IrregularCadenceCannotBiasTheAverageTowardRepeatedLowPower()
    {
        var times = new[] { 0, 1, 2, 3, 4, 5, 10, 20, 30, 40, 50, 60 };
        var samples = times.Select(t => new SolarActual(Hour.AddMinutes(t), t / 10d, SolarPowerBasis.PvDc)).ToArray();
        Assert.Equal(3, SolarHistoryAggregation.MeanPower(samples, Hour));
    }

    [Fact]
    public void MissingCoverageIsAGapButObservedNightIsZero()
    {
        var night = Enumerable.Range(0, 13).Select(i => new SolarActual(Hour.AddMinutes(i * 5), 0, SolarPowerBasis.PvDc)).ToArray();
        Assert.Equal(0, SolarHistoryAggregation.MeanPower(night, Hour));
        Assert.Null(SolarHistoryAggregation.MeanPower([], Hour));
        Assert.Null(SolarHistoryAggregation.MeanPower(night.Where(p => p.Timestamp < Hour.AddMinutes(20) || p.Timestamp > Hour.AddMinutes(40)).ToArray(), Hour));
        Assert.Null(SolarHistoryAggregation.MeanPower(night.Select(p => p with { Basis = SolarPowerBasis.GridExport }).ToArray(), Hour));
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void RequiresNinetyPercentCoverage(int missingMinutes, bool present)
    {
        var times = Enumerable.Range(missingMinutes, 61 - missingMinutes).Select(i => new SolarActual(Hour.AddMinutes(i), 2, SolarPowerBasis.PvDc)).ToArray();
        var result = SolarHistoryAggregation.MeanPower(times, Hour);
        Assert.Equal(present, result.HasValue);
        if (present) Assert.Equal(2, result);
    }

    [Fact]
    public void ClipsAdjacentMeasurementsAtHourBoundariesWithoutTakingNeighborHours()
    {
        var samples = Enumerable.Range(-1, 15).Select(i => new SolarActual(Hour.AddMinutes(i * 5), i * .5 + 1, SolarPowerBasis.PvDc)).ToArray();
        Assert.Equal(4, SolarHistoryAggregation.MeanPower(samples, Hour));
    }

    [Fact]
    public async Task CalculatesBothSeriesIndependentlyAndSharesWeatherBetweenPeriods()
    {
        var fixture = new Fixture();
        using var service = fixture.Service();
        var today = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        var week = await service.ReadAsync(SolarHistoryPeriod.Week, default);
        Assert.Equal(1, fixture.Weather.Calls);
        Assert.Equal(2, fixture.Store.Calls);
        Assert.Equal("selected", fixture.Store.Device);
        var point = today.Points.Single(p => p.Timestamp == Hour.AddHours(-1));
        // With no thermal/loss uncertainty, the 25% model + 8% configuration envelope
        // spans 8.1 kWp times 0.67–1.33 at 1000 W/m².
        AssertRange(point.Possible, 5.427, 10.773);
        Assert.Equal(2, point.ActualKw);
        Assert.Equal(point, week.Points.Single(p => p.Timestamp == point.Timestamp));
        Assert.All(today.Points, p => Assert.True(p.Timestamp.AddHours(1) <= fixture.Clock.Now));
    }

    [Fact]
    public async Task PossibleRangePreservesBothPhysicalBoundsWithoutSubstitutingTheCentralValue()
    {
        var fixture = new Fixture();
        fixture.Config.CurrentValue.ModelUncertaintyFraction = .2;
        fixture.Config.CurrentValue.ConfigurationUncertaintyFraction = 0;
        fixture.Config.CurrentValue.MinimumDcLossFraction = .05;
        fixture.Config.CurrentValue.DcLossFraction = .1;
        fixture.Config.CurrentValue.MaximumDcLossFraction = .15;
        using var service = fixture.Service();
        var result = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        var point = result.Points.Single(p => p.Timestamp == Hour.AddHours(-1));

        // Independent endpoints: 8.1 × 0.8 × 0.85 and 8.1 × 1.2 × 0.95.
        // The central 7.29 kW is neither endpoint; the upper bound can exceed STC.
        AssertRange(point.Possible, 5.508, 9.234);
        Assert.True(point.Possible!.LowerKw < 7.29);
        Assert.True(point.Possible.UpperKw > 8.1);
        Assert.Equal(2, point.ActualKw);
    }

    [Fact]
    public async Task EachRoofContributesItsOwnRadiationToBothBounds()
    {
        var fixture = new Fixture();
        fixture.Weather.Samples = [new(Hour.AddHours(-1), 1000, 500, 20, 1, 0)];
        fixture.Config.CurrentValue.ModelUncertaintyFraction = .2;
        fixture.Config.CurrentValue.ConfigurationUncertaintyFraction = 0;
        using var service = fixture.Service();
        var result = await service.ReadAsync(SolarHistoryPeriod.Today, default);

        // 4.32 kWp × 1 + 3.78 kWp × 0.5 = 6.21 kW before the 20% envelope.
        AssertRange(result.Points.Single(p => p.Timestamp == Hour.AddHours(-1)).Possible, 4.968, 7.452);
    }

    [Fact]
    public async Task NightIsAZeroWidthRangeWhileMissingHoursRemainAbsent()
    {
        var fixture = new Fixture();
        fixture.Weather.Samples = [new(Hour.AddHours(-3), 0, 0, 20, 1, 0), new(Hour.AddHours(-1), 1000, 1000, 20, 1, 0)];
        using var service = fixture.Service();
        var result = await service.ReadAsync(SolarHistoryPeriod.Today, default);

        AssertRange(result.Points.Single(p => p.Timestamp == Hour.AddHours(-3)).Possible, 0, 0);
        Assert.Null(result.Points.Single(p => p.Timestamp == Hour.AddHours(-2)).Possible);
        AssertRange(result.Points.Single(p => p.Timestamp == Hour.AddHours(-1)).Possible, 5.427, 10.773);
    }

    [Fact]
    public async Task MissingThermalWeatherUsesTheExistingWiderPhysicalEnvelope()
    {
        var fixture = new Fixture();
        var config = fixture.Config.CurrentValue;
        config.TemperatureCoefficient = -.004;
        config.ModelUncertaintyFraction = 0;
        config.ConfigurationUncertaintyFraction = 0;
        config.FaimanU0 = 25;
        config.FaimanU1 = 0;
        config.CellTemperatureRiseAt1000 = 0;
        config.CellTemperatureUncertaintyC = 10;
        fixture.Weather.Samples = [new(Hour.AddHours(-1), 500, 500, 20, 2, 0)];
        using var service = fixture.Service();
        var complete = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        var completePoint = complete.Points.Single(p => p.Timestamp == Hour.AddHours(-1));

        // At 500 W/m² the cell is 40°C; ±10°C yields factors 0.90 and 0.98.
        AssertRange(completePoint.Possible, 3.645, 3.969);
        fixture.Weather.Samples = [new(Hour.AddHours(-1), 500, 500, null, null, 0)];
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(16);
        var missing = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        var missingPoint = missing.Points.Single(p => p.Timestamp == Hour.AddHours(-1));

        // The existing missing-weather rule adds 20°C: factors become 0.82 and 1.06.
        AssertRange(missingPoint.Possible, 3.321, 4.293);
        Assert.Equal(completePoint.ActualKw, missingPoint.ActualKw);
        Assert.Null(missing.WeatherError);
    }

    [Fact]
    public async Task ChangingActualPowerDoesNotCalibrateOrMoveThePossibleRange()
    {
        var fixture = new Fixture();
        using var service = fixture.Service();
        var before = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        fixture.Store.Samples = Enumerable.Range(0, 13)
            .Select(i => new SolarActual(Hour.AddHours(-1).AddMinutes(i * 5), 15, SolarPowerBasis.PvDc)).ToArray();
        var after = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        var beforePoint = before.Points.Single(p => p.Timestamp == Hour.AddHours(-1));
        var afterPoint = after.Points.Single(p => p.Timestamp == Hour.AddHours(-1));

        Assert.Equal(beforePoint.Possible, afterPoint.Possible);
        AssertRange(afterPoint.Possible, 5.427, 10.773);
        Assert.Equal(2, beforePoint.ActualKw);
        Assert.Equal(15, afterPoint.ActualKw);
        Assert.Equal(1, fixture.Weather.Calls);
        Assert.Equal(2, fixture.Store.Calls);
    }

    [Fact]
    public async Task NoWeatherOrMeasurementsLeavesBothSeriesMissingWithoutInventedZeroBounds()
    {
        var fixture = new Fixture();
        fixture.Weather.Samples = [];
        fixture.Store.Samples = [];
        using var service = fixture.Service();
        var result = await service.ReadAsync(SolarHistoryPeriod.Today, default);

        Assert.NotEmpty(result.Points);
        Assert.All(result.Points, point =>
        {
            Assert.Null(point.Possible);
            Assert.Null(point.ActualKw);
        });
        Assert.Equal(new DateOnly(2026, 9, 29), result.SelectedDate);
    }

    [Fact]
    public async Task SelectingPreviousDayReturnsItsCompleteCalendarRange()
    {
        var fixture = new Fixture();
        using var service = fixture.Service();
        var result = await service.ReadAsync(SolarHistoryPeriod.Today, default, new DateOnly(2026, 9, 28));

        Assert.Equal(new DateTimeOffset(2026, 9, 27, 22, 0, 0, TimeSpan.Zero), result.Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 22, 0, 0, TimeSpan.Zero), result.End);
        Assert.Equal(24, result.Points.Count);
        Assert.Equal(result.Start, result.Points[0].Timestamp);
        Assert.Equal(result.End.AddHours(-1), result.Points[^1].Timestamp);
        Assert.Equal(new DateOnly(2026, 9, 28), result.SelectedDate);
        Assert.Equal(new DateOnly(2026, 9, 29), result.Today);
    }

    [Fact]
    public async Task SelectedPastDayBoundsMeasuredQueryInsteadOfReadingThroughToday()
    {
        var fixture = new Fixture();
        using var service = fixture.Service();
        await service.ReadAsync(SolarHistoryPeriod.Today, default, new DateOnly(2026, 9, 28));

        Assert.Equal(new DateTimeOffset(2026, 9, 27, 21, 50, 0, TimeSpan.Zero), fixture.Store.Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 22, 10, 0, TimeSpan.Zero), fixture.Store.End);
    }

    [Fact]
    public async Task ReturningToTodayChangesBothSeriesAndReplacesTheHistoricalCacheWindow()
    {
        var fixture = new Fixture();
        var previousHour = Hour.AddDays(-1).AddHours(-1);
        var currentHour = Hour.AddHours(-1);
        fixture.Weather.Samples = [new(previousHour, 1000, 1000, 20, 1, 0), new(currentHour, 500, 500, 20, 1, 0)];
        fixture.Store.Samples = Enumerable.Range(0, 13)
            .Select(i => new SolarActual(previousHour.AddMinutes(i * 5), 2, SolarPowerBasis.PvDc))
            .Concat(Enumerable.Range(0, 13).Select(i => new SolarActual(currentHour.AddMinutes(i * 5), 4, SolarPowerBasis.PvDc)))
            .ToArray();
        using var service = fixture.Service();
        var yesterday = await service.ReadAsync(SolarHistoryPeriod.Today, default, new DateOnly(2026, 9, 28));
        var previousWeek = await service.ReadAsync(SolarHistoryPeriod.Week, default, new DateOnly(2026, 9, 28));
        Assert.Equal(1, fixture.Weather.Calls);
        Assert.Equal(new DateTimeOffset(2026, 8, 29, 22, 0, 0, TimeSpan.Zero), fixture.Weather.Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 22, 0, 0, TimeSpan.Zero), fixture.Weather.End);
        var yesterdayPoint = yesterday.Points.Single(p => p.Timestamp == previousHour);
        AssertRange(yesterdayPoint.Possible, 5.427, 10.773);
        Assert.Equal(2, yesterdayPoint.ActualKw);
        Assert.Equal(yesterdayPoint, previousWeek.Points.Single(p => p.Timestamp == previousHour));
        var today = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        Assert.Equal(2, fixture.Weather.Calls);
        Assert.Equal(new DateOnly(2026, 9, 29), today.SelectedDate);
        Assert.Equal(today.Today, today.SelectedDate);
        Assert.Equal(Hour, today.End);
        Assert.DoesNotContain(today.Points, point => point.Timestamp == previousHour);
        var todayPoint = today.Points.Single(p => p.Timestamp == currentHour);
        AssertRange(todayPoint.Possible, 2.7135, 5.3865);
        Assert.Equal(4, todayPoint.ActualKw);
        Assert.Equal(Hour.AddMinutes(10), fixture.Store.End);
    }

    [Theory]
    [InlineData(2026, 3, 29, 23, 22, 23)]
    [InlineData(2026, 10, 25, 22, 23, 25)]
    public void CompletePastDayUsesBothLocalMidnightsAcrossDst(int year, int month, int day,
        int previousUtcHour, int nextUtcHour, int hours)
    {
        var selected = new DateOnly(year, month, day);
        var now = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero).AddDays(1);
        var range = SolarHistoryAggregation.Range(SolarHistoryPeriod.Today, now, "Europe/Warsaw", selected);
        Assert.Equal(new DateTimeOffset(year, month, day, previousUtcHour, 0, 0, TimeSpan.Zero).AddDays(-1), range.Start);
        Assert.Equal(new DateTimeOffset(year, month, day, nextUtcHour, 0, 0, TimeSpan.Zero), range.End);
        Assert.Equal(hours, (range.End - range.Start).TotalHours);
    }

    [Theory]
    [InlineData(SolarHistoryPeriod.Week, 7)]
    [InlineData(SolarHistoryPeriod.Month, 30)]
    public async Task EarliestSelectableDateKeepsWeatherAndMeasuredRequestsBounded(SolarHistoryPeriod period, int days)
    {
        var fixture = new Fixture();
        using var service = fixture.Service();
        var result = await service.ReadAsync(period, default, new DateOnly(2026, 8, 31));
        var expectedEnd = new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedEnd.AddDays(-days), result.Start);
        Assert.Equal(expectedEnd, result.End);
        Assert.Equal(days * 24, result.Points.Count);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 22, 0, 0, TimeSpan.Zero), fixture.Weather.Start);
        Assert.Equal(expectedEnd, fixture.Weather.End);
        Assert.Equal(result.Start.AddMinutes(-10), fixture.Store.Start);
        Assert.Equal(result.End.AddMinutes(10), fixture.Store.End);
        Assert.True(fixture.Store.End - fixture.Store.Start < TimeSpan.FromDays(31));
        Assert.True(fixture.Weather.End - fixture.Weather.Start <= TimeSpan.FromDays(31));
    }

    [Fact]
    public async Task HistoricalMonthAcrossAutumnDstStillFitsSourceAndStoreLimits()
    {
        var fixture = new Fixture();
        fixture.Clock.Now = new DateTimeOffset(2026, 10, 31, 10, 30, 0, TimeSpan.Zero);
        using var service = fixture.Service();
        var result = await service.ReadAsync(SolarHistoryPeriod.Month, default, new DateOnly(2026, 10, 25));
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 22, 0, 0, TimeSpan.Zero), result.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero), result.End);
        Assert.Equal(721, result.Points.Count);
        Assert.True(fixture.Weather.End - fixture.Weather.Start < TimeSpan.FromDays(31));
        Assert.True(fixture.Store.End - fixture.Store.Start < TimeSpan.FromDays(31));
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(1)]
    public async Task OutOfBoundsSelectedDateIsRejectedBeforeSourcesAreRead(int dayOffset)
    {
        var fixture = new Fixture();
        using var service = fixture.Service();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReadAsync(SolarHistoryPeriod.Today,
            default, new DateOnly(2026, 9, 29).AddDays(dayOffset)));
        Assert.Equal(0, fixture.Weather.Calls);
        Assert.Equal(0, fixture.Store.Calls);
    }

    [Fact]
    public async Task PreviousDayRemainsAvailableBeforeTodaysFirstCompletedHour()
    {
        var fixture = new Fixture();
        fixture.Clock.Now = new DateTimeOffset(2026, 9, 28, 22, 30, 0, TimeSpan.Zero);
        using var service = fixture.Service();
        var today = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        Assert.Empty(today.Points);
        Assert.Equal(new DateOnly(2026, 9, 29), today.Today);
        Assert.Equal(today.Today, today.SelectedDate);
        Assert.Equal(0, fixture.Weather.Calls);
        Assert.Equal(0, fixture.Store.Calls);
        var previous = await service.ReadAsync(SolarHistoryPeriod.Today, default, today.Today.AddDays(-1));
        Assert.Equal(24, previous.Points.Count);
        Assert.Equal(new DateOnly(2026, 9, 28), previous.SelectedDate);
        Assert.Equal(today.Today, previous.Today);
    }

    [Fact]
    public async Task TodayQueryNeverReadsMeasurementsAfterNow()
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Hour.AddMinutes(3);
        using var service = fixture.Service();
        await service.ReadAsync(SolarHistoryPeriod.Today, default);
        Assert.Equal(fixture.Clock.Now, fixture.Store.End);
    }

    [Fact]
    public async Task MidnightRolloverCompletesThePreviouslySelectedDate()
    {
        var fixture = new Fixture();
        fixture.Clock.Now = new DateTimeOffset(2026, 9, 29, 21, 59, 0, TimeSpan.Zero);
        using var service = fixture.Service();
        var before = await service.ReadAsync(SolarHistoryPeriod.Today, default, new DateOnly(2026, 9, 29));
        Assert.Equal(23, before.Points.Count);
        fixture.Clock.Now = new DateTimeOffset(2026, 9, 29, 22, 1, 0, TimeSpan.Zero);
        var after = await service.ReadAsync(SolarHistoryPeriod.Today, default, new DateOnly(2026, 9, 29));
        Assert.Equal(24, after.Points.Count);
        Assert.Equal(new DateOnly(2026, 9, 29), after.SelectedDate);
        Assert.Equal(new DateOnly(2026, 9, 30), after.Today);
        Assert.Equal(before.End.AddHours(1), after.End);
        Assert.Equal(2, fixture.Weather.Calls);
    }

    [Fact]
    public async Task ConfigurationChangePreservesSelectedDateMetadataWhileDiscardingPoints()
    {
        var fixture = new Fixture();
        fixture.Store.OnRead = () => fixture.Deye.CurrentValue = new() { DeviceSn = "replacement" };
        using var service = fixture.Service();
        var result = await service.ReadAsync(SolarHistoryPeriod.Today, default, new DateOnly(2026, 9, 28));
        Assert.Empty(result.Points);
        Assert.NotNull(result.ActualError);
        Assert.Equal(new DateOnly(2026, 9, 28), result.SelectedDate);
        Assert.Equal(new DateOnly(2026, 9, 29), result.Today);
    }

    [Fact]
    public async Task FailedRefreshForAnotherDateCannotReuseThePreviousDateCache()
    {
        var fixture = new Fixture();
        using var service = fixture.Service();
        await service.ReadAsync(SolarHistoryPeriod.Today, default);
        fixture.Weather.Fail = true;
        var previous = await service.ReadAsync(SolarHistoryPeriod.Today, default, new DateOnly(2026, 9, 28));
        Assert.NotNull(previous.WeatherError);
        Assert.All(previous.Points, point => Assert.Null(point.Possible));
        Assert.Equal(2, fixture.Weather.Calls);
        Assert.Equal(new DateOnly(2026, 9, 28), previous.SelectedDate);
    }

    [Fact]
    public async Task CancelledHistoricalReadCanRetryTheSameDateWithoutCachedFailure()
    {
        var fixture = new Fixture();
        fixture.Weather.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        using var service = fixture.Service();
        using var cancellation = new CancellationTokenSource();
        var selected = new DateOnly(2026, 9, 28);
        var cancelled = service.ReadAsync(SolarHistoryPeriod.Today, cancellation.Token, selected);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        fixture.Weather.Gate = null;
        var retried = await service.ReadAsync(SolarHistoryPeriod.Today, default, selected);
        Assert.Null(retried.WeatherError);
        Assert.Equal(selected, retried.SelectedDate);
        Assert.Equal(2, fixture.Weather.Calls);
    }

    [Fact]
    public async Task OneSourceFailureLeavesTheOtherVisibleAndCachesFailureBriefly()
    {
        var fixture = new Fixture();
        fixture.Weather.Fail = true;
        using var service = fixture.Service();
        var result = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        Assert.NotNull(result.WeatherError);
        Assert.Null(result.ActualError);
        Assert.Contains(result.Points, p => p.ActualKw == 2);
        Assert.All(result.Points, p => Assert.Null(p.Possible));
        await service.ReadAsync(SolarHistoryPeriod.Week, default);
        Assert.Equal(1, fixture.Weather.Calls);
        fixture.Clock.Now += TimeSpan.FromMinutes(3);
        fixture.Weather.Fail = false;
        Assert.Null((await service.ReadAsync(SolarHistoryPeriod.Today, default)).WeatherError);
        Assert.Equal(2, fixture.Weather.Calls);
    }

    [Fact]
    public async Task ActualFailureDoesNotRemoveWeatherCurve()
    {
        var fixture = new Fixture();
        fixture.Store.Fail = true;
        using var service = fixture.Service();
        var result = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        Assert.NotNull(result.ActualError);
        Assert.Null(result.WeatherError);
        AssertRange(result.Points.Single(p => p.Timestamp == Hour.AddHours(-1)).Possible, 5.427, 10.773);
    }

    [Fact]
    public async Task UnconfirmedDeviceIsNotQueriedOrPresentedAsComparable()
    {
        var fixture = new Fixture();
        fixture.Config.CurrentValue.DeyeConfirmedDeviceSn = "other-device";
        using var service = fixture.Service();
        var result = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        Assert.Equal(0, fixture.Store.Calls);
        Assert.NotNull(result.ActualError);
        Assert.All(result.Points, p => Assert.Null(p.ActualKw));
        AssertRange(result.Points.Single(p => p.Timestamp == Hour.AddHours(-1)).Possible, 5.427, 10.773);
    }

    [Fact]
    public async Task DeviceChangeDuringReadDiscardsThePreviousDeviceResult()
    {
        var fixture = new Fixture();
        fixture.Store.OnRead = () => fixture.Deye.CurrentValue = new() { DeviceSn = "replacement" };
        using var service = fixture.Service();
        var result = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        Assert.Empty(result.Points);
        Assert.NotNull(result.ActualError);
    }

    [Fact]
    public async Task GeometryChangeInvalidatesWeatherAndRecalculates()
    {
        var fixture = new Fixture();
        using var service = fixture.Service();
        await service.ReadAsync(SolarHistoryPeriod.Today, default);
        fixture.Config.CurrentValue.Roof1Kwp = 5;
        var changed = await service.ReadAsync(SolarHistoryPeriod.Today, default);
        Assert.Equal(2, fixture.Weather.Calls);
        AssertRange(changed.Points.Single(p => p.Timestamp == Hour.AddHours(-1)).Possible, 5.8826, 11.6774);
    }

    [Fact]
    public async Task ConcurrentLoadsShareWeatherRequestAndCancelledLoadCanRetry()
    {
        var fixture = new Fixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Weather.Gate = gate.Task;
        using var service = fixture.Service();
        using var cancellation = new CancellationTokenSource();
        var cancelled = service.ReadAsync(SolarHistoryPeriod.Today, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var concurrentGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Weather.Gate = concurrentGate.Task;
        var first = service.ReadAsync(SolarHistoryPeriod.Today, default);
        var second = service.ReadAsync(SolarHistoryPeriod.Week, default);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(2, fixture.Weather.Calls);
        concurrentGate.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.Null(result.WeatherError));
        Assert.Equal(2, fixture.Weather.Calls);
    }

    private static void AssertRange(SolarHistoryPowerRange? range, double lower, double upper)
    {
        Assert.NotNull(range);
        Assert.Equal(lower, range.LowerKw, 10);
        Assert.Equal(upper, range.UpperKw, 10);
    }

    private sealed class Fixture
    {
        public Weather Weather { get; } = new();
        public Store Store { get; } = new();
        public Clock Clock { get; } = new();
        public Monitor<SolarEstimateOptions> Config { get; } = new(new() { DeyeSolarPowerIsPvDcConfirmed = true,
            DeyeConfirmedDeviceSn = "selected", TemperatureCoefficient = 0, TemperatureCoefficientUncertainty = 0,
            MinimumDcLossFraction = 0, DcLossFraction = 0, MaximumDcLossFraction = 0 });
        public Monitor<DeyeCloudOptions> Deye { get; } = new(new() { DeviceSn = "selected" });
        public SolarHistoryService Service() => new(Weather, Store, Config, Deye, Clock, NullLogger<SolarHistoryService>.Instance);
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = Hour.AddMinutes(30); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    private sealed class Weather : ISolarHistoryRadiationSource
    {
        public int Calls;
        public bool Fail;
        public Task? Gate;
        public DateTimeOffset Start;
        public DateTimeOffset End;
        public IReadOnlyList<SolarWeatherSample>? Samples;
        public async Task<IReadOnlyList<SolarWeatherSample>> ReadAsync(SolarEstimateOptions options, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        {
            Calls++;
            Start = start;
            End = end;
            if (Gate is not null) await Gate.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (Fail) throw new HttpRequestException();
            return Samples ?? [new(Hour.AddHours(-1), 1000, 1000, 20, 1, 0)];
        }
    }
    private sealed class Store : ISolarHistoryStore
    {
        public int Calls;
        public string? Device;
        public DateTimeOffset Start;
        public DateTimeOffset End;
        public IReadOnlyList<SolarActual>? Samples;
        public bool Fail;
        public Action? OnRead;
        public Task<IReadOnlyList<SolarActual>> ReadAsync(string deviceSn, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Device = deviceSn;
            Start = start;
            End = end;
            OnRead?.Invoke();
            if (Fail) throw new InvalidOperationException();
            return Task.FromResult<IReadOnlyList<SolarActual>>(Samples ?? Enumerable.Range(0, 13)
                .Select(i => new SolarActual(Hour.AddHours(-1).AddMinutes(i * 5), 2, SolarPowerBasis.PvDc)).ToArray());
        }
    }
}

using SolarManagement.Inverters.Contracts;
using DeyeSolar.Infrastructure.Settlement;
using System.Net;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tests;

public class ExportSalesServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 27, 22, 0, 0, TimeSpan.Zero);
    private static readonly ExportSalesRequest Day = new(ExportSalesPeriod.Day, new DateOnly(2026, 9, 28));

    [Theory]
    [InlineData(ExportSalesPeriod.Day, 24)]
    [InlineData(ExportSalesPeriod.Week, 7)]
    [InlineData(ExportSalesPeriod.RollingMonth, 30)]
    [InlineData(ExportSalesPeriod.Month, 31)]
    [InlineData(ExportSalesPeriod.Year, 12)]
    [InlineData(ExportSalesPeriod.Custom, 30)]
    public async Task UpcomingFiltersReturnUnknownBucketsWithoutFetchingOrValuingFutureData(ExportSalesPeriod period, int buckets)
    {
        var fixture = new Fixture();
        var date = new DateOnly(2027, 1, 1);
        var request = new ExportSalesRequest(period, date,
            period == ExportSalesPeriod.Custom ? date : null,
            period == ExportSalesPeriod.Custom ? date.AddDays(29) : null) { AllowFuture = true };

        var result = await fixture.Service.ReadDetailsAsync(request, default);

        Assert.Equal(request, result.Request);
        Assert.Equal(buckets, result.Buckets.Count);
        Assert.All(result.Buckets, bucket =>
        {
            Assert.Null(bucket.ExportKwh);
            Assert.Null(bucket.EnergyValuePln);
            Assert.Null(bucket.EstimatedDepositPln);
            Assert.Equal((0, 0, 0), (bucket.ExpectedHours, bucket.ObservedHours, bucket.ValuedHours));
        });
        Assert.Null(result.ExportKwh);
        Assert.Null(result.EnergyValuePln);
        Assert.Null(result.CurrentHour);
        Assert.Null(result.DataError);
        Assert.Null(result.PriceError);
        Assert.Empty(result.Hours!);
        Assert.Empty(result.MissingPriceHours!);
        Assert.Empty(fixture.Readings.Reads);
        Assert.Empty(fixture.Readings.Writes);
        Assert.Empty(fixture.History.Calls);
        Assert.Empty(fixture.PriceStore.Reads);
        Assert.Empty(fixture.PriceStore.Saves);
        Assert.Empty(fixture.Prices.Calls);
    }

    [Fact]
    public async Task MixedFutureRangeValuesOnlyCompletedHoursAndLeavesUpcomingBucketsUnavailable()
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Start.AddHours(1);
        fixture.Readings.Rows["selected"] = Constant(-1000, 1).ToList();
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 500m));
        var request = new ExportSalesRequest(ExportSalesPeriod.Custom, Day.Date, Day.Date, Day.Date.AddDays(6)) { AllowFuture = true };

        var result = await fixture.Service.ReadDetailsAsync(request, default);

        Assert.Equal(1m, result.ExportKwh);
        Assert.Equal(0.5m, result.EnergyValuePln);
        Assert.Equal((1, 1, 1), (result.ExpectedHours, result.ObservedHours, result.ValuedHours));
        Assert.Single(result.Hours!);
        Assert.Equal(7, result.Buckets.Count);
        Assert.All(result.Buckets.Skip(1), bucket =>
        {
            Assert.Null(bucket.ExportKwh);
            Assert.Null(bucket.EnergyValuePln);
            Assert.Equal(0, bucket.ExpectedHours);
        });
        Assert.Equal(Start.AddHours(1), Assert.Single(fixture.PriceStore.Reads).End);
        Assert.Empty(fixture.History.Calls);
        Assert.Empty(fixture.Prices.Calls);
    }

    [Fact]
    public async Task HourlyDetailsAreOptInAndNeverChangeTheLegacyAppStorePayload()
    {
        var fixture = new Fixture();
        fixture.Readings.Rows["selected"] = Constant(-1000, 1).ToList();
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 200m));
        var legacy = await fixture.Service.ReadAsync(Day, default);
        var details = await fixture.Service.ReadDetailsAsync(Day, default);
        Assert.Null(legacy.Hours);
        Assert.Null(legacy.MissingPriceHours);
        var json = System.Text.Json.JsonSerializer.Serialize(legacy, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.DoesNotContain("\"hours\"", json);
        Assert.DoesNotContain("\"missingPriceHours\"", json);
        Assert.DoesNotContain("allowFuture", json);
        var hour = Assert.Single(details.Hours!);
        Assert.Equal(1m, hour.ExportKwh);
        Assert.Equal(0.2m, hour.AveragePricePlnPerKwh);
        Assert.Empty(details.MissingPriceHours!);
        Assert.Equal(legacy.EnergyValuePln, details.EnergyValuePln);
        Assert.Equal(legacy.Buckets, details.Buckets);
    }

    [Fact]
    public async Task ExplicitPriceRecheckCanRetryImmediatelyAndPreservesPreviouslyStoredIntervals()
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Start.AddHours(2).AddMinutes(10);
        fixture.Readings.Rows["selected"] = Constant(-1000, 2).ToList();
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 200m));
        fixture.PriceStore.Rows.AddRange(PriceHour(Start.AddHours(1), 300m).Take(3));
        fixture.Prices.Fail = true;
        var partial = await fixture.Service.ReadDetailsAsync(Day, default);
        Assert.Equal(Start.AddHours(1), Assert.Single(partial.MissingPriceHours!));
        await fixture.Service.ReadDetailsAsync(Day, default);
        Assert.Single(fixture.Prices.Calls);
        fixture.Prices.Fail = false;
        fixture.Prices.Rows = [new(Start.AddMinutes(105), Start.AddHours(2), 300m)];
        var complete = await fixture.Service.RecheckPricesAsync(Day, default);
        Assert.Equal(2, fixture.Prices.Calls.Count);
        Assert.Empty(complete.MissingPriceHours!);
        Assert.Equal(0.5m, complete.EnergyValuePln);
        Assert.Equal(8, fixture.PriceStore.Rows.Count);
        Assert.Equal(200m, fixture.PriceStore.Rows.Single(price => price.Start == Start).PricePlnPerMwh);
    }

    [Fact]
    public async Task ExplicitPriceRecheckIncludesUnpublishedHoursWithMeasuredZeroCredit()
    {
        var fixture = new Fixture();
        fixture.Readings.Rows["selected"] = Constant(1000, 1).ToList();
        var first = await fixture.Service.ReadDetailsAsync(Day, default);
        Assert.Equal(0m, first.EnergyValuePln);
        Assert.Equal(Start, Assert.Single(first.MissingPriceHours!));
        Assert.Empty(fixture.Prices.Calls);
        fixture.Prices.Rows = PriceHour(Start, -200m);
        var checkedPrices = await fixture.Service.RecheckPricesAsync(Day, default);
        Assert.Single(fixture.Prices.Calls);
        Assert.Empty(checkedPrices.MissingPriceHours!);
        var hour=Assert.Single(checkedPrices.Hours!);
        Assert.Equal(-0.2m, hour.MarketAveragePricePlnPerKwh);
        Assert.Equal(0m,hour.AveragePricePlnPerKwh);
        Assert.Equal(0m, checkedPrices.EnergyValuePln);
    }

    [Fact]
    public async Task ValuesHourlyNetExportInsteadOfGrossExportAndKeepsSignedPricePolicyExplicit()
    {
        var fixture = new Fixture();
        // 1.5 kWh exported and 0.75 kWh imported: only 0.75 kWh is credited for the hour.
        int[] watts = [-4000, -4000, -4000, -4000, -4000, -4000, -3200, -2400, -1600, -800, 0,
            400, 800, 1200, 1600, 2000, 2000, 2000, 2000, 2000, 2000];
        fixture.Readings.Rows["selected"] = watts.Select((value, index) => new ExportGridSample(Start.AddMinutes(index * 3), value)).ToList();
        fixture.Readings.Rows["selected"].Add(new(Start.AddMinutes(65), 2000));
        fixture.Readings.Rows["neighbor"] = Constant(-99000, 1).ToList();
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 100m, -200m, 300m, 400m));

        var first = await fixture.Service.ReadAsync(Day, default);

        Assert.Equal(1.5m, first.ExportKwh);
        Assert.Equal(0.75m, first.CreditedExportKwh);
        Assert.Equal(0.15m, first.EnergyValuePln);
        Assert.Equal(0.1845m, first.EstimatedDepositPln);
        Assert.Equal((1, 1, 1), (first.ExpectedHours, first.ObservedHours, first.ValuedHours));
        Assert.False(first.IsPartial);
        Assert.Empty(fixture.History.Calls);
        Assert.Empty(fixture.Prices.Calls);
        Assert.Equal(-99000, fixture.Readings.Rows["neighbor"][0].GridPowerWatts);

        fixture.Options.CurrentValue.PayNegativePrices = true;
        var amendedPolicy = await fixture.Service.ReadAsync(Day, default);
        Assert.Equal(0.1125m, amendedPolicy.EnergyValuePln);
        Assert.Equal(0.138375m, amendedPolicy.EstimatedDepositPln);
        Assert.Equal(first.CreditedExportKwh, amendedPolicy.CreditedExportKwh);
        Assert.Equal(-200m, fixture.PriceStore.Rows[1].PricePlnPerMwh);
    }

    [Fact]
    public async Task MissingQuarterLeavesOneHourUnvaluedAndLaterPublicationPreservesKnownPrices()
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Start.AddHours(2).AddMinutes(10);
        fixture.Readings.Rows["selected"] = Constant(-2000, 2).ToList();
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 100m));
        fixture.PriceStore.Rows.AddRange(PriceHour(Start.AddHours(1), 100m).Take(3));
        fixture.Prices.Fail = true;

        var partial = await fixture.Service.ReadAsync(Day, default);

        Assert.Equal(4m, partial.ExportKwh);
        Assert.Equal(4m, partial.CreditedExportKwh);
        Assert.Equal(0.2m, partial.EnergyValuePln);
        Assert.Equal(0.246m, partial.EstimatedDepositPln);
        Assert.Equal((2, 2, 1), (partial.ExpectedHours, partial.ObservedHours, partial.ValuedHours));
        Assert.True(partial.IsPartial);
        Assert.Equal("PSE prices are temporarily unavailable. Stored prices are preserved; missing intervals have no value estimate.", partial.PriceError);
        Assert.Null(partial.DataError);
        Assert.Equal(new Window(Start.AddHours(1), Start.AddHours(2)), Assert.Single(fixture.Prices.Calls));
        Assert.Equal(7, fixture.PriceStore.Rows.Count);

        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3);
        fixture.Prices.Fail = false;
        fixture.Prices.Rows = [new(Start.AddMinutes(105), Start.AddHours(2), 100m)];
        var complete = await fixture.Service.ReadAsync(Day, default);

        Assert.Equal(0.4m, complete.EnergyValuePln);
        Assert.Equal(0.492m, complete.EstimatedDepositPln);
        Assert.Equal(2, complete.ValuedHours);
        Assert.False(complete.IsPartial);
        Assert.Null(complete.PriceError);
        Assert.Equal(8, fixture.PriceStore.Rows.Count);
        Assert.Single(fixture.PriceStore.Saves);
        Assert.Single(fixture.PriceStore.Saves[0]);
    }

    [Fact]
    public async Task FailedPricePersistenceDoesNotClaimFetchedMoneyIsDurablyAvailable()
    {
        var fixture = new Fixture();
        fixture.Readings.Rows["selected"] = Constant(-1000, 1).ToList();
        fixture.Prices.Rows = PriceHour(Start, 200m);
        fixture.PriceStore.FailSave = true;

        var result = await fixture.Service.ReadAsync(Day, default);

        Assert.Equal(1m, result.CreditedExportKwh);
        Assert.Null(result.EnergyValuePln);
        Assert.Null(result.EstimatedDepositPln);
        Assert.Equal((1, 1, 0), (result.ExpectedHours, result.ObservedHours, result.ValuedHours));
        Assert.True(result.IsPartial);
        Assert.Equal("PSE prices are temporarily unavailable. Stored prices are preserved; missing intervals have no value estimate.", result.PriceError);
        Assert.Empty(fixture.PriceStore.Rows);
    }

    [Fact]
    public async Task ZeroHourlyCreditIsKnownWithoutInventingMissingPrices()
    {
        var fixture = new Fixture();
        fixture.Readings.Rows["selected"] = Constant(1500, 1).ToList();
        fixture.Prices.Fail = true;

        var result = await fixture.Service.ReadAsync(Day, default);

        Assert.Equal(0m, result.ExportKwh);
        Assert.Equal(0m, result.CreditedExportKwh);
        Assert.Equal(0m, result.EnergyValuePln);
        Assert.Equal(1, result.ValuedHours);
        Assert.False(result.IsPartial);
        Assert.Empty(fixture.Prices.Calls);
        Assert.Empty(fixture.PriceStore.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableReadingsOrMissingDeviceKeepEligibleHoursUnknown(bool missingDevice)
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Start.AddHours(3).AddMinutes(20);
        if (missingDevice) fixture.Devices.CurrentValue.DeviceKey = "";
        else fixture.Readings.FailReadOnCall = 1;

        var result = await fixture.Service.ReadAsync(Day, default);

        Assert.Equal((3, 0, 0), (result.ExpectedHours, result.ObservedHours, result.ValuedHours));
        Assert.Null(result.ExportKwh);
        Assert.Null(result.EnergyValuePln);
        Assert.True(result.IsPartial);
        Assert.Equal(missingDevice ? "Select an inverter in Settings."
            : "Inverter history is unavailable. Refresh the page to try again.", result.DataError);
        Assert.Empty(fixture.History.Calls);
        Assert.Empty(fixture.Prices.Calls);
        Assert.Empty(fixture.PriceStore.Saves);
    }

    [Fact]
    public async Task FailedRereadAfterImportDoesNotEraseTheExpectedHourCount()
    {
        var fixture = new Fixture();
        fixture.History.Rows = Constant(-1000, 1);
        fixture.Readings.FailReadOnCall = 2;

        var result = await fixture.Service.ReadAsync(Day, default);

        Assert.Single(fixture.Readings.Writes);
        Assert.Equal((1, 0, 0), (result.ExpectedHours, result.ObservedHours, result.ValuedHours));
        Assert.True(result.IsPartial);
        Assert.Null(result.EnergyValuePln);
        Assert.Equal("History was saved but is temporarily unavailable. Refresh the page to try again.", result.DataError);
        Assert.Empty(fixture.Prices.Calls);
    }

    [Fact]
    public async Task FailedHistoryPersistenceRemainsUnknownAndCanRecoverWithoutDuplicatingObservations()
    {
        var fixture = new Fixture();
        fixture.History.Rows = Constant(-1000, 1);
        fixture.Readings.FailWrite = true;
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 100m));

        var failed = await fixture.Service.ReadAsync(Day, default);

        Assert.Equal((1, 0, 0), (failed.ExpectedHours, failed.ObservedHours, failed.ValuedHours));
        Assert.Null(failed.ExportKwh);
        Assert.Null(failed.EnergyValuePln);
        Assert.Equal("Some Inverter history is unavailable. Totals include only complete hours with data.", failed.DataError);
        Assert.Empty(fixture.Readings.Rows);
        Assert.Empty(fixture.Readings.Writes);
        Assert.Empty(fixture.Prices.Calls);

        fixture.Readings.FailWrite = false;
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3);
        var recovered = await fixture.Service.ReadAsync(Day, default);

        Assert.Equal(0.1m, recovered.EnergyValuePln);
        Assert.False(recovered.IsPartial);
        Assert.Equal(2, fixture.History.Calls.Count);
        Assert.Single(fixture.Readings.Writes);
        Assert.Equal(13, fixture.Readings.Rows["selected"].Count);
    }

    [Fact]
    public async Task EntirelyPrecontractDateHasNoReadOrWriteSideEffects()
    {
        var fixture = new Fixture();
        var result = await fixture.Service.ReadAsync(new(ExportSalesPeriod.Day, new DateOnly(2026, 9, 27)), default);

        Assert.Equal((0, 0, 0), (result.ExpectedHours, result.ObservedHours, result.ValuedHours));
        Assert.Null(result.ExportKwh);
        Assert.Null(result.EnergyValuePln);
        Assert.Null(result.CurrentHour);
        Assert.Empty(fixture.Readings.Reads);
        Assert.Empty(fixture.Readings.Writes);
        Assert.Empty(fixture.History.Calls);
        Assert.Empty(fixture.PriceStore.Reads);
        Assert.Empty(fixture.Prices.Calls);
        Assert.Empty(fixture.PriceStore.Saves);
    }

    [Fact]
    public async Task BackfillQueriesUtcBoundaryNeighborsAndOnlyTheSelectedDevice()
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Start.AddHours(1).AddMinutes(20);
        fixture.History.Rows = Enumerable.Range(0, 15)
            .Select(index => new ExportGridSample(Start.AddMinutes(-3 + index * 5), -1000)).ToArray();
        fixture.Readings.Rows["neighbor"] = Constant(-9000, 1).ToList();
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 100m));

        var result = await fixture.Service.ReadAsync(Day, default);

        var call = Assert.Single(fixture.History.Calls);
        Assert.Equal("selected", call.Device);
        Assert.Equal(Start.AddMinutes(-10), call.Start);
        Assert.Equal(fixture.Clock.Now, call.End);
        Assert.All(fixture.Readings.Reads, read =>
        {
            Assert.Equal(call.Device, read.Device);
            Assert.Equal(call.Start, read.Start);
            Assert.Equal(fixture.Clock.Now.AddTicks(1), read.End);
        });
        Assert.Equal(1m, result.ExportKwh);
        Assert.Equal(0.1m, result.EnergyValuePln);
        Assert.Equal(0.123m, result.EstimatedDepositPln);
        Assert.False(result.IsPartial);
        Assert.Equal(24, result.Buckets.Count);
        Assert.Null(result.Buckets[1].ExportKwh);
        Assert.Equal(-9000, fixture.Readings.Rows["neighbor"][0].GridPowerWatts);
        Assert.All(fixture.Readings.Writes, write => Assert.Equal("selected", write.Device));
    }

    [Fact]
    public async Task LargeBackfillIsCappedAndThrottledWindowsDoNotBlockTheNextPart()
    {
        var fixture = new Fixture();
        fixture.Options.CurrentValue.ContractStartDate = new DateOnly(2026, 9, 1);
        fixture.Clock.Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var request = new ExportSalesRequest(ExportSalesPeriod.Custom, Day.Date,
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 29));

        var first = await fixture.Service.ReadAsync(request, default);
        var firstCalls = fixture.History.Calls.ToArray();
        var second = await fixture.Service.ReadAsync(request, default);
        var secondCalls = fixture.History.Calls.Skip(firstCalls.Length).ToArray();

        Assert.Equal(14, firstCalls.Length);
        Assert.Equal(14, secondCalls.Length);
        Assert.Empty(firstCalls.Intersect(secondCalls));
        Assert.All(fixture.History.Calls, call => Assert.InRange(call.End - call.Start, TimeSpan.FromSeconds(1), TimeSpan.FromHours(6)));
        Assert.Equal(29 * 24, first.ExpectedHours);
        Assert.Equal(first.ExpectedHours, second.ExpectedHours);
        Assert.Equal(0, first.ObservedHours);
        Assert.Null(first.EnergyValuePln);
        Assert.True(first.IsPartial);
        Assert.Equal("History is loading in batches. Refresh the page to load the next part.", first.DataError);

        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(6);
        await fixture.Service.ReadAsync(request, default);
        var thirdCalls = fixture.History.Calls.Skip(28).ToArray();
        Assert.Empty(firstCalls.Concat(secondCalls).Intersect(thirdCalls));
        Assert.Equal(secondCalls[^1].End, thirdCalls[0].Start);
        Assert.Equal(42, fixture.History.Calls.Count);
    }

    [Fact]
    public async Task AnnualBackfillPrioritizesCurrentProgressAndAdvancesUnattemptedHistoryAtFiveMinuteRefreshes()
    {
        var fixture = new Fixture();
        fixture.Options.CurrentValue.ContractStartDate = new(2026, 1, 1);
        var currentStart = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        fixture.Clock.Now = currentStart.AddMinutes(20);
        fixture.History.Read = (_, from, through, _) =>
        {
            if (from > currentStart || through <= currentStart)
                return Task.FromResult<IReadOnlyList<ExportGridSample>>([]);
            var latest = fixture.Clock.Now.AddMinutes(-5);
            var samples = Enumerable.Range(-1, latest.Minute / 5 + 2)
                .Select(index => new ExportGridSample(currentStart.AddMinutes(index * 5), -1000)).ToArray();
            return Task.FromResult<IReadOnlyList<ExportGridSample>>(samples);
        };
        var request = new ExportSalesRequest(ExportSalesPeriod.Year, new(2026, 9, 30));

        var first = await fixture.Service.ReadAsync(request, default);
        var firstCalls = fixture.History.Calls.ToArray();

        Assert.Equal(14, firstCalls.Length);
        Assert.True(firstCalls[0].Start <= currentStart && firstCalls[0].End > currentStart);
        var current = Assert.IsType<ExportSaleProgress>(first.CurrentHour);
        Assert.Equal(currentStart.AddMinutes(15), current.ObservedThrough);
        Assert.Equal(0.25m, current.ExportKwh);
        Assert.Null(current.EnergyValuePln);
        Assert.Null(first.ExportKwh);
        Assert.Null(first.EnergyValuePln);
        Assert.Equal((6541, 0, 0), (first.ExpectedHours, first.ObservedHours, first.ValuedHours));
        Assert.True(first.IsPartial);

        fixture.Clock.Now = currentStart.AddMinutes(25);
        var second = await fixture.Service.ReadAsync(request, default);
        var secondCalls = fixture.History.Calls.Skip(14).ToArray();

        Assert.Equal(14, secondCalls.Length);
        Assert.Empty(firstCalls.Select(call => call.Start).Intersect(secondCalls.Select(call => call.Start)));
        Assert.Equal(firstCalls[^1].End, secondCalls[0].Start);
        Assert.Equal(currentStart.AddMinutes(15), Assert.IsType<ExportSaleProgress>(second.CurrentHour).ObservedThrough);
        Assert.Equal((6541, 0, 0), (second.ExpectedHours, second.ObservedHours, second.ValuedHours));
        Assert.Null(second.EnergyValuePln);

        fixture.Clock.Now = currentStart.AddMinutes(30);
        var third = await fixture.Service.ReadAsync(request, default);
        var thirdCalls = fixture.History.Calls.Skip(28).ToArray();

        Assert.Equal(14, thirdCalls.Length);
        Assert.Equal(firstCalls[0].Start, thirdCalls[0].Start);
        Assert.Empty(firstCalls.Concat(secondCalls).Select(call => call.Start)
            .Intersect(thirdCalls.Skip(1).Select(call => call.Start)));
        current = Assert.IsType<ExportSaleProgress>(third.CurrentHour);
        Assert.Equal(currentStart.AddMinutes(25), current.ObservedThrough);
        Assert.Equal(0.416667m, decimal.Round(current.ExportKwh!.Value, 6));
        Assert.Null(current.EnergyValuePln);
        Assert.Equal((6541, 0, 0), (third.ExpectedHours, third.ObservedHours, third.ValuedHours));
        Assert.Null(third.EnergyValuePln);
        Assert.All(fixture.History.Calls, call => Assert.InRange(call.End - call.Start,
            TimeSpan.FromSeconds(1), TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task HistoryFailureIsRetriedAfterItsBoundedThrottleWithoutFabricatedZeros()
    {
        var fixture = new Fixture();
        fixture.History.Fail = true;
        var first = await fixture.Service.ReadAsync(Day, default);
        var second = await fixture.Service.ReadAsync(Day, default);
        Assert.Single(fixture.History.Calls);
        Assert.Equal(1, first.ExpectedHours);
        Assert.Equal(0, second.ObservedHours);
        Assert.Null(second.ExportKwh);
        Assert.Null(second.EnergyValuePln);
        Assert.Empty(fixture.Readings.Writes);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3);
        await fixture.Service.ReadAsync(Day, default);
        Assert.Equal(2, fixture.History.Calls.Count);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("contract")]
    [InlineData("timezone")]
    [InlineData("price-policy")]
    public async Task ConfigurationReplacementDuringHistoryCannotPersistOrRenderTheOldResult(string change)
    {
        var fixture = new Fixture();
        fixture.History.Read = (_, _, _, _) =>
        {
            switch (change)
            {
                case "device": fixture.Devices.CurrentValue.DeviceKey = "replacement"; break;
                case "contract": fixture.Options.CurrentValue.ContractStartDate = new DateOnly(2026, 9, 29); break;
                case "timezone": fixture.Options.CurrentValue.TimeZoneId = "UTC"; break;
                default: fixture.Options.CurrentValue.PayNegativePrices = true; break;
            }
            return Task.FromResult<IReadOnlyList<ExportGridSample>>(Constant(-3000, 1));
        };

        var result = await fixture.Service.ReadAsync(Day, default);

        Assert.Empty(fixture.Readings.Writes);
        Assert.Empty(fixture.PriceStore.Saves);
        Assert.Empty(fixture.Prices.Calls);
        Assert.Null(result.ExportKwh);
        Assert.Null(result.EnergyValuePln);
        Assert.Equal("Installation settings changed. Refresh the page.", result.DataError);
        Assert.True(result.IsPartial);
    }

    [Fact]
    public async Task DeviceReplacementDuringPriceFetchCannotPersistOrRenderThatResult()
    {
        var fixture = new Fixture();
        fixture.Readings.Rows["selected"] = Constant(-1000, 1).ToList();
        fixture.Prices.Read = (_, _, _) =>
        {
            fixture.Devices.CurrentValue.DeviceKey = "replacement";
            return Task.FromResult<IReadOnlyList<ExportPriceInterval>>(PriceHour(Start, 500m));
        };

        var result = await fixture.Service.ReadAsync(Day, default);

        Assert.Empty(fixture.PriceStore.Saves);
        Assert.Null(result.EnergyValuePln);
        Assert.Null(result.ExportKwh);
        Assert.Equal("Installation settings changed. Refresh the page.", result.DataError);
        Assert.Equal(13, fixture.Readings.Rows["selected"].Count);
    }

    [Fact]
    public async Task CancellationDuringHistoryReleasesTheGateAndAllowsAnImmediateRetry()
    {
        var fixture = new Fixture();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.History.Read = async (_, _, _, ct) =>
        {
            started.SetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        };
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Service.ReadAsync(Day, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(fixture.Readings.Writes);
        Assert.Empty(fixture.PriceStore.Saves);

        fixture.History.Read = null;
        fixture.History.Rows = Constant(-1000, 1);
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 100m));
        var result = await fixture.Service.ReadAsync(Day, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.History.Calls.Count);
        Assert.Equal(0.1m, result.EnergyValuePln);
        Assert.False(result.IsPartial);
    }

    [Fact]
    public async Task CancellationDuringPriceFetchDoesNotPersistOrThrottleTheCancelledRequest()
    {
        var fixture = new Fixture();
        fixture.Readings.Rows["selected"] = Constant(-1000, 1).ToList();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Prices.Read = async (_, _, ct) =>
        {
            started.SetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        };
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Service.ReadAsync(Day, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(fixture.PriceStore.Saves);

        fixture.Prices.Read = null;
        fixture.Prices.Rows = PriceHour(Start, 100m);
        var result = await fixture.Service.ReadAsync(Day, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.Prices.Calls.Count);
        Assert.Equal(0.1m, result.EnergyValuePln);
        Assert.Single(fixture.PriceStore.Saves);
    }

    [Theory]
    [InlineData("measurement-gap", "There are gaps in the measurements. Totals include only complete hours with data.")]
    [InlineData("price-gap", "Prices are not published for every hour. The total value includes only hours with available prices.")]
    [InlineData("price-storage", "Stored prices are unavailable. The total value includes only hours with known prices.")]
    public async Task MissingDataAndPriceMessagesRemainEnglish(string scenario, string expected)
    {
        var fixture = new Fixture();
        if (scenario == "price-gap") fixture.Readings.Rows["selected"] = Constant(-1000, 1).ToList();
        if (scenario == "price-storage")
        {
            fixture.Readings.Rows["selected"] = Constant(1000, 1).ToList();
            fixture.PriceStore.FailRead = true;
        }

        var result = await fixture.Service.ReadAsync(Day, default);

        Assert.Equal(expected, scenario == "measurement-gap" ? result.DataError : result.PriceError);
        foreach (var message in new[] { result.DataError, result.PriceError }.OfType<string>())
            Assert.DoesNotMatch("[\\u0400-\\u04FF]", message);
    }

    [Fact]
    public async Task FirstContractHourRefreshesFromFiveMinuteMeasurementsWithoutEnteringCompletedTotals()
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Start.AddMinutes(20);
        fixture.Readings.Rows["selected"] = Enumerable.Range(0, 4)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), -1000)).ToList();
        fixture.Readings.Rows["selected"].Add(new(Start.AddMinutes(55), -9000));
        fixture.Readings.Rows["neighbor"] = Constant(-9000, 1).ToList();
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 500m));
        decimal[] expectedKwh = [0.25m, 0.333333m, 0.416667m, 0.5m];
        decimal[] expectedValue = [0.125m, 0.166667m, 0.208333m, 0.25m];

        for (var index = 0; index < 4; index++)
        {
            var latest = Start.AddMinutes(15 + index * 5);
            fixture.Clock.Now = latest.AddMinutes(5);
            if (index > 0) fixture.Readings.Rows["selected"].Add(new(latest, -1000));

            var result = await fixture.Service.ReadAsync(Day, default);

            var current = Assert.IsType<ExportSaleProgress>(result.CurrentHour);
            Assert.Equal(Start, current.Start);
            Assert.Equal(latest, current.ObservedThrough);
            Assert.Equal(900 + index * 300, current.ObservedSeconds);
            Assert.Equal(expectedKwh[index], decimal.Round(current.ExportKwh!.Value, 6));
            Assert.Equal(expectedValue[index], decimal.Round(current.EnergyValuePln!.Value, 6));
            Assert.Equal(fixture.Clock.Now, result.UpdatedAt);
            Assert.Equal((0, 0, 0), (result.ExpectedHours, result.ObservedHours, result.ValuedHours));
            Assert.Null(result.ExportKwh);
            Assert.Null(result.EnergyValuePln);
            Assert.All(result.Buckets, bucket => Assert.Null(bucket.ExportKwh));
            Assert.Equal(fixture.Clock.Now.AddTicks(1), fixture.Readings.Reads[^1].End);
            Assert.Equal(Start.AddHours(1), fixture.PriceStore.Reads[^1].End);
        }
        Assert.Empty(fixture.History.Calls);
        Assert.Empty(fixture.Prices.Calls);
        Assert.Empty(fixture.Readings.Writes);
        Assert.Equal(-9000, fixture.Readings.Rows["neighbor"][0].GridPowerWatts);
    }

    [Fact]
    public async Task ObservationExactlyAtCapturedNowIsIncludedButFutureObservationIsNot()
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Start.AddMinutes(30);
        fixture.Readings.Rows["selected"] = Enumerable.Range(0, 7)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), -1000)).ToList();
        fixture.Readings.Rows["selected"].Add(new(fixture.Clock.Now.AddTicks(1), -9000));
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 500m));

        var result = await fixture.Service.ReadAsync(Day, default);

        var current = Assert.IsType<ExportSaleProgress>(result.CurrentHour);
        Assert.Equal(fixture.Clock.Now, current.ObservedThrough);
        Assert.Equal(1800, current.ObservedSeconds);
        Assert.Equal(0.5m, current.ExportKwh);
        Assert.Equal(0.25m, current.EnergyValuePln);
        Assert.Equal(0.3075m, current.EstimatedDepositPln);
        Assert.Empty(fixture.History.Calls);
        Assert.Empty(fixture.Prices.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task CurrentMoneyRequiresAllFourPricesIncludingQuartersAfterTheObservation(int missingQuarter)
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Start.AddMinutes(20);
        fixture.Readings.Rows["selected"] = Enumerable.Range(0, 4)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), -1000)).ToList();
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 500m).Where((_, index) => index != missingQuarter));

        var result = await fixture.Service.ReadAsync(Day, default);

        var current = Assert.IsType<ExportSaleProgress>(result.CurrentHour);
        Assert.Equal(Start.AddMinutes(15), current.ObservedThrough);
        Assert.Equal(0.25m, current.ExportKwh);
        Assert.Null(current.EnergyValuePln);
        Assert.Null(current.EstimatedDepositPln);
        Assert.Equal(new Window(Start, Start.AddHours(1)), Assert.Single(fixture.Prices.Calls));
        Assert.Empty(fixture.History.Calls);
        Assert.Equal(3, fixture.PriceStore.Rows.Count);
        Assert.Null(result.EnergyValuePln);
    }

    [Fact]
    public async Task StaleProgressRemainsMeasuredAsOfItsTimestampWhileBackfillCanAdvanceIt()
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Start.AddMinutes(35);
        fixture.Readings.Rows["selected"] = Enumerable.Range(0, 4)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), -1000)).ToList();
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 500m));
        fixture.History.Fail = true;

        var stale = await fixture.Service.ReadAsync(Day, default);

        var current = Assert.IsType<ExportSaleProgress>(stale.CurrentHour);
        Assert.Equal(Start.AddMinutes(15), current.ObservedThrough);
        Assert.Equal(0.25m, current.ExportKwh);
        Assert.Equal(0.125m, current.EnergyValuePln);
        Assert.Equal(fixture.Clock.Now, stale.UpdatedAt);
        Assert.NotNull(stale.DataError);
        Assert.Equal(fixture.Clock.Now, Assert.Single(fixture.History.Calls).End);

        fixture.Clock.Now = Start.AddMinutes(40);
        fixture.History.Fail = false;
        fixture.History.Rows = Enumerable.Range(0, 8)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), -1000)).ToArray();
        var refreshed = await fixture.Service.ReadAsync(Day, default);
        current = Assert.IsType<ExportSaleProgress>(refreshed.CurrentHour);
        Assert.Equal(Start.AddMinutes(35), current.ObservedThrough);
        Assert.Equal(2100, current.ObservedSeconds);
        Assert.Equal(0.583333m, decimal.Round(current.ExportKwh!.Value, 6));
        Assert.Equal(2, fixture.History.Calls.Count);
        Assert.Single(fixture.Readings.Writes);
        Assert.Null(refreshed.DataError);
        Assert.Equal(0, refreshed.ExpectedHours);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("storage")]
    [InlineData("history")]
    [InlineData("gap")]
    public async Task FirstHourFailuresRetainUnknownProgressInsteadOfInventingZeros(string failure)
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Start.AddMinutes(20);
        if (failure == "device") fixture.Devices.CurrentValue.DeviceKey = "";
        if (failure == "storage") fixture.Readings.FailReadOnCall = 1;
        if (failure == "history") fixture.History.Fail = true;
        if (failure == "gap") fixture.Readings.Rows["selected"] = [new(Start, -1000), new(Start.AddMinutes(15), -1000)];

        var result = await fixture.Service.ReadAsync(Day, default);

        var current = Assert.IsType<ExportSaleProgress>(result.CurrentHour);
        Assert.Equal(Start, current.Start);
        Assert.Null(current.ObservedThrough);
        Assert.Null(current.ExportKwh);
        Assert.Null(current.CreditedExportKwh);
        Assert.Null(current.EnergyValuePln);
        Assert.Null(current.EstimatedDepositPln);
        Assert.Equal((0, 0, 0), (result.ExpectedHours, result.ObservedHours, result.ValuedHours));
        Assert.Equal(fixture.Clock.Now, result.UpdatedAt);
        Assert.Empty(fixture.Prices.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactHourRolloverNeedsTheBoundaryObservationAndHasNoCurrentProgress(bool boundaryPresent)
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Start.AddHours(1);
        fixture.Readings.Rows["selected"] = Enumerable.Range(0, boundaryPresent ? 13 : 12)
            .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), -1000)).ToList();
        fixture.Readings.Rows["selected"].Add(new(Start.AddMinutes(65), -1000));
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 500m));

        var result = await fixture.Service.ReadAsync(Day, default);

        Assert.Null(result.CurrentHour);
        Assert.Equal(1, result.ExpectedHours);
        Assert.Equal(boundaryPresent ? 1 : 0, result.ObservedHours);
        Assert.Equal(boundaryPresent ? 1m : (decimal?)null, result.ExportKwh);
        Assert.Equal(boundaryPresent ? 0.5m : (decimal?)null, result.EnergyValuePln);
        Assert.Equal(fixture.Clock.Now, result.UpdatedAt);
        Assert.Equal(boundaryPresent ? 0 : 1, fixture.History.Calls.Count);
    }

    [Theory]
    [InlineData(ExportSalesPeriod.Day, 2026, 10, 25, 0, 2)]
    [InlineData(ExportSalesPeriod.Month, 2026, 10, 25, 1, 3)]
    [InlineData(ExportSalesPeriod.Year, 2026, 10, 25, 1, 3)]
    [InlineData(ExportSalesPeriod.Custom, 2028, 2, 29, 0, 1)]
    public async Task CurrentCalendarPeriodsKeepDstAndLeapDayProgressSeparateFromCompletedHours(
        ExportSalesPeriod period, int year, int month, int day, int hour, int completedHours)
    {
        var currentStart = new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero);
        var contractStart = currentStart.AddHours(-completedHours);
        var date = new DateOnly(year, month, day);
        var fixture = new Fixture();
        fixture.Options.CurrentValue.ContractStartDate = date;
        fixture.Clock.Now = currentStart.AddMinutes(35);
        fixture.Readings.Rows["selected"] = Enumerable.Range(0, completedHours * 12 + 7)
            .Select(index => new ExportGridSample(contractStart.AddMinutes(index * 5), -1000)).ToList();
        for (var index = 0; index <= completedHours; index++)
            fixture.PriceStore.Rows.AddRange(PriceHour(contractStart.AddHours(index), 500m));

        var result = await fixture.Service.ReadAsync(new(period, date, date, date), default);

        var current = Assert.IsType<ExportSaleProgress>(result.CurrentHour);
        Assert.Equal(currentStart, current.Start);
        Assert.Equal(currentStart.AddMinutes(30), current.ObservedThrough);
        Assert.Equal(0.5m, current.ExportKwh);
        Assert.Equal(0.25m, current.EnergyValuePln);
        Assert.Equal(completedHours, result.ExpectedHours);
        Assert.Equal((decimal)completedHours, result.ExportKwh);
        Assert.Equal(completedHours * 0.5m, result.EnergyValuePln);
        Assert.Equal(currentStart.AddHours(1), Assert.Single(fixture.PriceStore.Reads).End);
        Assert.Empty(fixture.History.Calls);
        Assert.Empty(fixture.Prices.Calls);
    }

    [Fact]
    public async Task QueuedCancellationAndDeviceChangeCannotPublishOrPersistOldCurrentProgress()
    {
        var fixture = new Fixture();
        fixture.Clock.Now = Start.AddMinutes(20);
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 500m));
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<ExportGridSample>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.History.Read = (_, _, _, _) =>
        {
            started.TrySetResult(true);
            return release.Task;
        };
        var first = fixture.Service.ReadAsync(Day, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var queued = fixture.Service.ReadAsync(Day, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Single(fixture.Readings.Reads);
        fixture.Devices.CurrentValue.DeviceKey = "replacement";
        var samples = Enumerable.Range(0, 4).Select(index => new ExportGridSample(Start.AddMinutes(index * 5), -1000)).ToArray();
        release.SetResult(samples);

        var discarded = await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(Assert.IsType<ExportSaleProgress>(discarded.CurrentHour).ExportKwh);
        Assert.Equal("Installation settings changed. Refresh the page.", discarded.DataError);
        Assert.Empty(fixture.Readings.Writes);
        Assert.Empty(fixture.PriceStore.Saves);
        fixture.History.Read = null;
        fixture.History.Rows = samples;
        var fresh = await fixture.Service.ReadAsync(Day, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0.25m, Assert.IsType<ExportSaleProgress>(fresh.CurrentHour).ExportKwh);
        Assert.Equal("replacement", Assert.Single(fixture.Readings.Writes).Device);
        Assert.False(fixture.Readings.Rows.ContainsKey("selected"));
    }

    [Fact]
    public async Task ManualPriceValuesReadingsWithoutLoadingOrReplacingSharedMarketPrices()
    {
        var fixture = new Fixture(); fixture.Clock.Now = Start.AddHours(1);
        fixture.Readings.Rows["selected"] = Constant(-1000, 1).ToList();
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 900m));
        fixture.Options.CurrentValue.PriceSource = "manual"; fixture.Options.CurrentValue.ManualPricePlnPerKwh = 0.321456m;
        var result = await fixture.Service.ReadAsync(Day, default);
        Assert.Equal(0.321456m, result.EnergyValuePln); Assert.Equal(0.321456m * 1.23m, result.EstimatedDepositPln);
        Assert.Empty(fixture.PriceStore.Reads); Assert.Empty(fixture.PriceStore.Saves); Assert.Empty(fixture.Prices.Calls);
        Assert.All(fixture.PriceStore.Rows, p => Assert.Equal(900m, p.PricePlnPerMwh));
    }

    [Fact]
    public async Task FeedUsesPrivateCacheAndRefreshesKnownIntervalsWhilePreservingOfficialPrices()
    {
        var fixture = new Fixture(); fixture.Clock.Now = Start.AddHours(1);
        fixture.Readings.Rows["selected"] = Constant(-1000, 1).ToList();
        fixture.PriceStore.Rows.AddRange(PriceHour(Start, 900m));
        fixture.Options.CurrentValue.PriceSource = "feed"; fixture.Options.CurrentValue.PriceFeedUrl = "https://example.com/feed.csv";
        fixture.Prices.Rows = PriceHour(Start, 300m);
        var first = await fixture.Service.ReadAsync(Day, default);
        Assert.Equal(0.3m, first.EnergyValuePln); Assert.Empty(fixture.PriceStore.Reads); Assert.Empty(fixture.PriceStore.Saves);
        Assert.Single(fixture.PriceStore.FeedSaves); Assert.Single(fixture.Prices.Calls);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3); fixture.Prices.Rows = PriceHour(Start, 400m);
        var refreshed = await fixture.Service.ReadAsync(Day, default);
        Assert.Equal(0.4m, refreshed.EnergyValuePln); Assert.Equal(2, fixture.Prices.Calls.Count);
        Assert.All(fixture.PriceStore.Rows, p => Assert.Equal(900m, p.PricePlnPerMwh));
        fixture.Options.CurrentValue.PriceFeedUrl = "https://example.com/different.xml"; fixture.Prices.Fail = true;
        var missing = await fixture.Service.ReadAsync(Day, default);
        Assert.Null(missing.EnergyValuePln); Assert.Equal(2, fixture.PriceStore.FeedRows.Count);
        Assert.Equal("The price feed is temporarily unavailable. Saved feed prices are preserved; missing intervals have no value estimate.", missing.PriceError);
    }

    [Fact]
    public async Task SwitchingFeedDuringFetchCannotPersistOrPublishThePreviousSourcesPrices()
    {
        var fixture = new Fixture(); fixture.Clock.Now = Start.AddHours(1);
        fixture.Readings.Rows["selected"] = Constant(-1000, 1).ToList();
        fixture.Options.CurrentValue.PriceSource = "feed"; fixture.Options.CurrentValue.PriceFeedUrl = "https://example.com/feed.csv";
        fixture.Prices.Read = (_, _, _) => { fixture.Options.CurrentValue.PriceFeedUrl = "https://example.com/other.csv"; return Task.FromResult<IReadOnlyList<ExportPriceInterval>>(PriceHour(Start, 300m)); };
        var result = await fixture.Service.ReadAsync(Day, default);
        Assert.Equal("Installation settings changed. Refresh the page.", result.DataError);
        Assert.Empty(fixture.PriceStore.FeedSaves); Assert.Empty(fixture.PriceStore.Saves); Assert.Null(result.EnergyValuePln);
    }

    [Fact]
    public async Task FeedFetchRemainsPinnedToCapturedSourceAcrossAnAbaConfigurationChange()
    {
        var fixture = new Fixture(); fixture.Clock.Now = Start.AddHours(1);
        fixture.Readings.Rows["selected"] = Constant(-1000, 1).ToList();
        fixture.Options.CurrentValue.PriceSource = "feed"; fixture.Options.CurrentValue.PriceFeedUrl = "https://example.com/a.csv";
        fixture.PriceStore.BeforeFeedRead = () => fixture.Options.CurrentValue.PriceFeedUrl = "https://example.com/b.csv";
        var requested = new List<Uri>();
        var handler = new FeedHandler(request =>
        {
            requested.Add(request.RequestUri!);
            fixture.Options.CurrentValue.PriceFeedUrl = "https://example.com/a.csv";
            return new(HttpStatusCode.OK) { Content = new StringContent("interval_start,interval_end,price_pln_per_kwh\n2026-09-27T22:00:00Z,2026-09-27T23:00:00Z,0.3") };
        });
        var source = new ConfiguredExportPriceSource(new PseExportPriceClient(new UnusedPseReader()),
            new ExportPriceFeedClient(new HttpClient(handler)), fixture.Options);
        var service = new ExportSalesService(fixture.Readings, fixture.History, fixture.PriceStore, source, fixture.Options, fixture.Devices,
            fixture.Clock, NullLogger<ExportSalesService>.Instance);
        var result = await service.ReadAsync(Day, default);
        Assert.Equal("https://example.com/a.csv", Assert.Single(requested).AbsoluteUri);
        Assert.Equal(0.3m, result.EnergyValuePln); Assert.Single(fixture.PriceStore.FeedSaves); Assert.Empty(fixture.PriceStore.Saves);
    }
    private sealed class FeedHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request)); }
    private sealed class UnusedPseReader : IPseJsonReader
    { public Task<JsonDocument> ReadAsync(Uri uri, CancellationToken ct) => throw new InvalidOperationException("A feed must not access PSE."); }

    [Fact]
    public async Task FailedFeedRefreshKeepsCachedValuesAndOutageVisibleThroughoutRetryThrottle()
    {
        var fixture = new Fixture(); fixture.Clock.Now = Start.AddHours(1);
        fixture.Readings.Rows["selected"] = Constant(-1000, 1).ToList();
        fixture.Options.CurrentValue.PriceSource = "feed"; fixture.Options.CurrentValue.PriceFeedUrl = "https://example.com/feed.csv";
        fixture.Prices.Rows = PriceHour(Start, 300m);
        Assert.Equal(0.3m, (await fixture.Service.ReadAsync(Day, default)).EnergyValuePln);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3); fixture.Prices.Fail = true;
        var failed = await fixture.Service.ReadAsync(Day, default);
        Assert.Equal(0.3m, failed.EnergyValuePln); Assert.NotNull(failed.PriceError);
        var calls = fixture.Prices.Calls.Count; fixture.Clock.Now = fixture.Clock.Now.AddSeconds(30);
        var throttled = await fixture.Service.ReadAsync(Day, default);
        Assert.Equal(calls, fixture.Prices.Calls.Count); Assert.Equal(0.3m, throttled.EnergyValuePln); Assert.Equal(failed.PriceError, throttled.PriceError);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2); fixture.Prices.Fail = false; fixture.Prices.Rows = PriceHour(Start, 400m);
        var recovered = await fixture.Service.ReadAsync(Day, default);
        Assert.Equal(0.4m, recovered.EnergyValuePln); Assert.Null(recovered.PriceError);
    }

    private static ExportGridSample[] Constant(int watts, int hours) => Enumerable.Range(0, hours * 12 + 1)
        .Select(index => new ExportGridSample(Start.AddMinutes(index * 5), watts)).ToArray();
    private static ExportPriceInterval[] PriceHour(DateTimeOffset from, params decimal[] values) => Enumerable.Range(0, 4)
        .Select(index => new ExportPriceInterval(from.AddMinutes(index * 15), from.AddMinutes((index + 1) * 15), values.Length == 1 ? values[0] : values[index])).ToArray();

    private sealed class Fixture
    {
        public Clock Clock { get; } = new();
        public FixedOptionsMonitor<SolarSalesOptions> Options { get; } = new(new());
        public FixedOptionsMonitor<InverterConnectionOptions> Devices { get; } = new(new() { DeviceKey = "selected" });
        public ReadingStore Readings { get; } = new();
        public HistorySource History { get; } = new();
        public PriceStore PriceStore { get; } = new();
        public PriceSource Prices { get; } = new();
        public ExportSalesService Service { get; }
        public Fixture() => Service = new(Readings, History, PriceStore, Prices, Options, Devices, Clock, NullLogger<ExportSalesService>.Instance);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = Start.AddHours(1).AddMinutes(10);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed record Window(DateTimeOffset Start, DateTimeOffset End);
    private sealed record DeviceWindow(string Device, DateTimeOffset Start, DateTimeOffset End);
    private sealed record HistoryWrite(string Device, IReadOnlyList<ExportGridSample> Samples);

    private sealed class ReadingStore : IExportReadingStore
    {
        public Dictionary<string, List<ExportGridSample>> Rows { get; } = new(StringComparer.Ordinal);
        public List<DeviceWindow> Reads { get; } = [];
        public List<HistoryWrite> Writes { get; } = [];
        public int? FailReadOnCall;
        public bool FailWrite;
        public Task<IReadOnlyList<ExportGridSample>> ReadAsync(string deviceSn, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads.Add(new(deviceSn, start, end));
            if (Reads.Count == FailReadOnCall) throw new IOException("Fixture storage unavailable.");
            return Task.FromResult<IReadOnlyList<ExportGridSample>>(Rows.TryGetValue(deviceSn, out var rows)
                ? rows.Where(sample => sample.Timestamp >= start && sample.Timestamp < end).OrderBy(sample => sample.Timestamp).ToArray() : []);
        }
        public Task UpsertHistoryAsync(string deviceSn, IReadOnlyList<ExportGridSample> samples, DateTimeOffset polledAt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailWrite) throw new IOException("Fixture history storage unavailable.");
            Writes.Add(new(deviceSn, samples));
            Rows.TryGetValue(deviceSn, out var rows);
            Rows[deviceSn] = (rows ?? []).Concat(samples).GroupBy(sample => sample.Timestamp).Select(group => group.Last()).ToList();
            return Task.CompletedTask;
        }
    }
    private sealed class HistorySource : IExportGridHistorySource
    {
        public IReadOnlyList<ExportGridSample> Rows = [];
        public List<DeviceWindow> Calls { get; } = [];
        public bool Fail;
        public Func<string, DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<ExportGridSample>>>? Read;
        public Task<IReadOnlyList<ExportGridSample>> ReadAsync(string deviceSn, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(new(deviceSn, start, end));
            if (Fail) throw new HttpRequestException("Fixture Deye unavailable.");
            return Read?.Invoke(deviceSn, start, end, ct) ?? Task.FromResult<IReadOnlyList<ExportGridSample>>(Rows.Where(sample => sample.Timestamp >= start && sample.Timestamp < end).ToArray());
        }
    }
    private sealed class PriceStore : IExportPriceStore, IScopedExportPriceStore
    {
        public Dictionary<string, List<ExportPriceInterval>> FeedRows { get; } = [];
        public List<string> FeedSaves { get; } = [];
        public Action? BeforeFeedRead;
        public Task<IReadOnlyList<ExportPriceInterval>> ReadFeedAsync(string sourceKey, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        {
            BeforeFeedRead?.Invoke();
            if (!FeedRows.ContainsKey(sourceKey)) FeedRows[sourceKey] = [];
            return Task.FromResult<IReadOnlyList<ExportPriceInterval>>(FeedRows[sourceKey].Where(p => p.Start >= start && p.Start < end).ToArray());
        }
        public Task SaveFeedAsync(string sourceKey, IReadOnlyList<ExportPriceInterval> prices, DateTimeOffset retrievedAt, CancellationToken ct)
        {
            FeedSaves.Add(sourceKey);
            if (!FeedRows.ContainsKey(sourceKey)) FeedRows[sourceKey] = [];
            foreach (var price in prices) { FeedRows[sourceKey].RemoveAll(p => p.Start == price.Start); FeedRows[sourceKey].Add(price); }
            return Task.CompletedTask;
        }
        public List<ExportPriceInterval> Rows { get; } = [];
        public List<Window> Reads { get; } = [];
        public List<IReadOnlyList<ExportPriceInterval>> Saves { get; } = [];
        public bool FailRead;
        public bool FailSave;
        public Task<IReadOnlyList<ExportPriceInterval>> ReadAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads.Add(new(start, end));
            if (FailRead) throw new IOException("Fixture price storage unavailable.");
            return Task.FromResult<IReadOnlyList<ExportPriceInterval>>(Rows.Where(price => price.Start >= start && price.Start < end).ToArray());
        }
        public Task SaveAsync(IReadOnlyList<ExportPriceInterval> prices, DateTimeOffset retrievedAt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailSave) throw new IOException("Fixture price storage unavailable.");
            Saves.Add(prices);
            foreach (var price in prices)
            {
                Rows.RemoveAll(row => row.Start == price.Start);
                Rows.Add(price);
            }
            return Task.CompletedTask;
        }
    }
    private sealed class PriceSource : IExportPriceSource
    {
        public IReadOnlyList<ExportPriceInterval> Rows = [];
        public List<Window> Calls { get; } = [];
        public bool Fail;
        public Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<ExportPriceInterval>>>? Read;
        public Task<IReadOnlyList<ExportPriceInterval>> ReadAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(new(start, end));
            if (Fail) throw new HttpRequestException("Fixture PSE unavailable.");
            return Read?.Invoke(start, end, ct) ?? Task.FromResult<IReadOnlyList<ExportPriceInterval>>(Rows.Where(price => price.Start >= start && price.Start < end).ToArray());
        }
    }
}

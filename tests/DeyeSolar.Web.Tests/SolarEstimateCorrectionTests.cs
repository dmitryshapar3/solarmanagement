using SolarPowerBasis = DeyeSolar.Domain.Models.SolarPowerBasis;
using SolarManagement.Inverters.Contracts;
using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tests;

public class SolarEstimateCorrectionTests
{
    private static readonly DateTime Observed = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [SqlServerFact]
    public async Task SqlServerComparisonAndHistoryUseLatestValidCorrection()
    {
        await WithDatabaseAsync(async factory =>
        {
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.Readings.Add(Row("selected", Observed, 1000));
                await seed.SaveChangesAsync();
                seed.Readings.Add(Row("selected", Observed, 2500));
                await seed.SaveChangesAsync();
                seed.Readings.AddRange(
                    Row("selected", Observed, -1),
                    Row("neighbor", Observed, 9000),
                    Row(null, Observed, 8000),
                    Row("selected", null, 7000),
                    Row("selected", Observed.AddSeconds(-121), 6000),
                    Row("selected", Observed.AddSeconds(121), 5000),
                    Row("selected", Observed.AddMinutes(10), 1500));
                await seed.SaveChangesAsync();
                seed.Readings.Add(Row("selected", Observed.AddMinutes(10), 0));
                await seed.SaveChangesAsync();
                seed.Readings.Add(Row("selected", Observed.AddMinutes(10), -1));
                var unusableCorrection = Row("selected", Observed, 99000);
                unusableCorrection.SolarPowerValid = false;
                seed.Readings.Add(unusableCorrection);
                seed.AppSettings.Add(new AppSetting { Section = "Neighbor", Key = "unchanged", Value = "preserved" });
                seed.RuleRunLogs.Add(new RuleRunLog { Timestamp = Observed, RuleName = "neighbor-rule" });
                await seed.SaveChangesAsync();
            }

            var before = await ReadStateAsync(factory);
            var comparison = new SolarEstimateStore(factory, new FixedOptionsMonitor<InverterConnectionOptions>(new() { DeviceKey = "selected" }));
            var history = new SolarHistoryStore(factory);
            for (var repeat = 0; repeat < 5; repeat++)
            {
                var corrected = await comparison.FindActualAsync(new(Observed), 120, new(Observed.AddMinutes(2)), default);
                Assert.NotNull(corrected);
                Assert.Equal(2.5, corrected.PowerKw);
                Assert.Equal(new DateTimeOffset(Observed), corrected.Timestamp);
                Assert.Equal(SolarPowerBasis.PvDc, corrected.Basis);
                var chart = Assert.Single(await history.ReadAsync("selected", new(Observed), new(Observed.AddMinutes(1)), default));
                Assert.Equal(2.5, chart.PowerKw);
                Assert.Equal(corrected, chart);

                var zeroTime = Observed.AddMinutes(10);
                var zero = await comparison.FindActualAsync(new(zeroTime), 120, new(zeroTime.AddMinutes(2)), default);
                Assert.NotNull(zero);
                Assert.Equal(0, zero.PowerKw);
                Assert.Equal(new DateTimeOffset(zeroTime), zero.Timestamp);
                Assert.Equal(zero, Assert.Single(await history.ReadAsync("selected", new(zeroTime), new(zeroTime.AddMinutes(1)), default)));
            }

            var neighbor = new SolarEstimateStore(factory, new FixedOptionsMonitor<InverterConnectionOptions>(new() { DeviceKey = "neighbor" }));
            Assert.Equal(9, (await neighbor.FindActualAsync(new(Observed), 120, new(Observed.AddMinutes(2)), default))!.PowerKw);
            Assert.Equal(9, Assert.Single(await history.ReadAsync("neighbor", new(Observed), new(Observed.AddMinutes(1)), default)).PowerKw);
            Assert.Null(await new SolarEstimateStore(factory, new FixedOptionsMonitor<InverterConnectionOptions>(new() { DeviceKey = "absent" }))
                .FindActualAsync(new(Observed), 120, new(Observed.AddMinutes(2)), default));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => comparison.FindActualAsync(new(Observed),
                120, new(Observed.AddMinutes(2)), cancellation.Token));
            Assert.Equal(before, await ReadStateAsync(factory));
        });
    }

    [SqlServerFact]
    public async Task SqlServerNearestReadingPreservesTimestampAndNowBoundaries()
    {
        await WithDatabaseAsync(async factory =>
        {
            var earliest = Observed.AddMinutes(-2);
            var latest = Observed.AddMinutes(2);
            await using (var seed = await factory.CreateDbContextAsync())
            {
                seed.Readings.Add(Row("selected", earliest, 1000));
                await seed.SaveChangesAsync();
                seed.Readings.Add(Row("selected", earliest, 3500));
                await seed.SaveChangesAsync();
                seed.Readings.Add(Row("selected", latest, 6500));
                await seed.SaveChangesAsync();
                seed.Readings.AddRange(
                    Row("selected", earliest.AddSeconds(-1), 4500),
                    Row("selected", latest.AddSeconds(10), 7500),
                    Row("selected", latest, -1),
                    Row("neighbor", Observed, 9000),
                    Row(null, Observed, 8000),
                    Row("selected", null, 7000));
                await seed.SaveChangesAsync();
            }

            var before = await ReadStateAsync(factory);
            var comparison = new SolarEstimateStore(factory, new FixedOptionsMonitor<InverterConnectionOptions>(new() { DeviceKey = "selected" }));
            for (var repeat = 0; repeat < 5; repeat++)
            {
                // Equal distances retain the earlier measured time, then its latest valid correction.
                var tied = await comparison.FindActualAsync(new(Observed), 120, new(latest), default);
                Assert.NotNull(tied);
                Assert.Equal(new DateTimeOffset(earliest), tied.Timestamp);
                Assert.Equal(3.5, tied.PowerKw);

                var atNow = await comparison.FindActualAsync(new(latest), 0, new(latest), default);
                Assert.NotNull(atNow);
                Assert.Equal(new DateTimeOffset(latest), atNow.Timestamp);
                Assert.Equal(6.5, atNow.PowerKw);

                var beforeFuture = await comparison.FindActualAsync(new(latest.AddSeconds(10)), 15, new(latest), default);
                Assert.Equal(atNow, beforeFuture);
                Assert.Null(await comparison.FindActualAsync(new(latest.AddSeconds(10)), 0, new(latest), default));
            }
            Assert.Equal(before, await ReadStateAsync(factory));
        });
    }

    private static Reading Row(string? device, DateTime? measured, int watts) => new()
    {
        Timestamp = Observed,
        SolarDeviceSn = device,
        SolarObservedAt = measured,
        SolarProduction = watts,
        SolarPowerValid = true,
        DataSource = "DeyeCloud"
    };

    private static async Task WithDatabaseAsync(Func<TenantDbContextFactory, Task> scenario)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync("SolarEstimateCorrectionTests", SqlTestSchema.Model, seed: TestInstallation.EnsureAsync);
        await scenario(database.Factory);
    }

    private static async Task<string> ReadStateAsync(TenantDbContextFactory factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return JsonSerializer.Serialize(new
        {
            Readings = await db.Readings.AsNoTracking().OrderBy(r => r.Id).ToArrayAsync(),
            Settings = await db.AppSettings.AsNoTracking().OrderBy(s => s.Id).ToArrayAsync(),
            Logs = await db.RuleRunLogs.AsNoTracking().OrderBy(r => r.Id).ToArrayAsync()
        });
    }

}

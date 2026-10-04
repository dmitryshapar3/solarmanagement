using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Workers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DeyeSolar.Web.Tests;

public class ExportReadingStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Start.AddDays(1); }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" selected ")]
    public async Task MissingOrAmbiguousDeviceNeverOpensDatabase(string device)
    {
        var store = new ExportReadingStore(new RejectingFactory(), new Clock());
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(device, Start, Start.AddHours(1), default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertHistoryAsync(device, [], Start, default));
    }

    [Fact]
    public async Task InvalidBatchIsRejectedBeforeAnyPersistence()
    {
        var store = new ExportReadingStore(new RejectingFactory(), new Clock());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpsertHistoryAsync("selected",
            [new(Start.AddMinutes(1), 1)], Start, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertHistoryAsync("selected",
            [new(Start, 1), new(Start, 2)], Start, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpsertHistoryAsync("selected",
            Enumerable.Repeat(new ExportGridSample(Start, 1), 301).ToArray(), Start, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadAsync("selected", Start, Start, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadAsync("selected", Start, Start.AddDays(368), default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpsertHistoryAsync("selected", [], Start, cancellation.Token));
    }

    [SqlServerFact]
    public async Task RepeatedPollingCorrectionsAndRetentionPreserveIndependentDurableDevices()
    {
        await using var database = await Database.CreateAsync();
        var store = database.Store;
        await store.UpsertHistoryAsync("neighbor", [new(Start, 8100)], Start.AddHours(1), default);
        await store.UpsertHistoryAsync("Selected", [new(Start, 6100)], Start.AddHours(1), default);
        await store.UpsertHistoryAsync("selected", [new(Start.AddDays(-60), -450)], Start, default);
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.Readings.Add(new Reading { Timestamp = Start.AddDays(-60).UtcDateTime, GridConsumption = -9999 });
            seed.AppSettings.Add(new AppSetting { Section = "Neighbor", Key = "unchanged", Value = "preserved" });
            seed.RuleRunLogs.Add(new RuleRunLog { Timestamp = Start.AddDays(-60).UtcDateTime, RuleName = "preserved" });
            await seed.SaveChangesAsync();
        }

        var original = Poll(-2500, Start.AddMinutes(10));
        await store.SavePollingAsync(original, default);
        await store.SavePollingAsync(original, default);
        await store.SavePollingAsync(Poll(-2000, Start.AddMinutes(11)), default);
        await store.SavePollingAsync(Poll(-9000, Start.AddMinutes(9)), default);
        await store.SavePollingAsync(new InverterData { GridConsumption = -7000, Timestamp = Start.AddMinutes(12) }, default);
        await store.UpsertHistoryAsync("selected", [new(Start.AddTicks(-1), -400), new(Start.AddMinutes(5), 300), new(Start.AddHours(1), -600)], Start.AddHours(2), default);

        var values = await store.ReadAsync("selected", Start, Start.AddHours(1), default);
        Assert.Equal(new[] { new ExportGridSample(Start, -2000), new(Start.AddMinutes(5), 300) }, values);
        Assert.Equal(8100, Assert.Single(await store.ReadAsync("neighbor", Start, Start.AddHours(1), default)).GridPowerWatts);
        Assert.Equal(6100, Assert.Single(await store.ReadAsync("Selected", Start, Start.AddHours(1), default)).GridPowerWatts);
        Assert.Empty(await store.ReadAsync("absent", Start, Start.AddHours(1), default));
        await using (var check = database.Factory.CreateDbContext())
        {
            Assert.Equal(6, await check.Readings.CountAsync());
            Assert.Equal(7, await check.ExportReadings.CountAsync());
            Assert.Equal(Start.AddMinutes(11).UtcDateTime, (await check.ExportReadings.SingleAsync(row => row.DeviceSn == "selected" && row.ObservedAt == Start.UtcDateTime)).PolledAt);
            Assert.Equal(1, await check.AppSettings.CountAsync());
            Assert.Equal(1, await check.RuleRunLogs.CountAsync());
        }

        await using (var cleanup = database.Factory.CreateDbContext())
            Assert.Equal(6, await PollingWorker.ExpiredReadings(cleanup.Readings, Start.AddDays(33).UtcDateTime).ExecuteDeleteAsync());
        await using var retained = database.Factory.CreateDbContext();
        Assert.Equal(7, await retained.ExportReadings.CountAsync());
        Assert.Equal("preserved", (await retained.AppSettings.SingleAsync()).Value);
        Assert.Equal("preserved", (await retained.RuleRunLogs.SingleAsync()).RuleName);
        Assert.Equal(values, await store.ReadAsync("selected", Start, Start.AddHours(1), default));
    }

    [SqlServerFact]
    public async Task FailedLegacySaveAndFailedHistoryBatchRollBackOnlyTheirOwnWork()
    {
        await using var database = await Database.CreateAsync();
        await database.Store.UpsertHistoryAsync("neighbor", [new(Start, 1500)], Start, default);
        await using (var constraints = database.Factory.CreateDbContext())
        {
            await constraints.Database.ExecuteSqlRawAsync("ALTER TABLE Readings ADD CONSTRAINT CK_TestReadingFailure CHECK (GridConsumption <> 778877);");
            await constraints.Database.ExecuteSqlRawAsync("ALTER TABLE ExportReadings ADD CONSTRAINT CK_TestExportFailure CHECK (GridPowerWatts <> 777777);");
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => database.Store.SavePollingAsync(Poll(778877, Start.AddMinutes(5)), default));
        await Assert.ThrowsAsync<SqlException>(() => database.Store.UpsertHistoryAsync("selected",
            [new(Start, -2000), new(Start.AddMinutes(5), 777777)], Start.AddHours(1), default));

        await using var check = database.Factory.CreateDbContext();
        Assert.Empty(await check.Readings.ToListAsync());
        var remaining = Assert.Single(await check.ExportReadings.ToListAsync());
        Assert.Equal("neighbor", remaining.DeviceSn);
        Assert.Equal(1500, remaining.GridPowerWatts);
        Assert.Equal(Start.UtcDateTime, remaining.PolledAt);
    }

    [SqlServerFact]
    public async Task ConcurrentOverlappingBatchesKeepOneLatestCorrectionAndRespectNeighbors()
    {
        await using var database = await Database.CreateAsync();
        var jobs = Enumerable.Range(1, 8).Select(index => database.Store.UpsertHistoryAsync("selected",
            [new(Start, -index * 100), new(Start.AddMinutes(5), index * 100)], Start.AddMinutes(index + 5), default));
        await Task.WhenAll(jobs.Append(database.Store.UpsertHistoryAsync("neighbor", [new(Start, 1234)], Start.AddHours(1), default)));
        var values = await database.Store.ReadAsync("selected", Start, Start.AddHours(1), default);
        Assert.Equal(new[] { -800, 800 }, values.Select(sample => sample.GridPowerWatts));
        Assert.Equal(1234, Assert.Single(await database.Store.ReadAsync("neighbor", Start, Start.AddHours(1), default)).GridPowerWatts);
        await using var check = database.Factory.CreateDbContext();
        Assert.Equal(3, await check.ExportReadings.CountAsync());
        Assert.Empty(await check.Readings.ToListAsync());
    }

    [SqlServerFact]
    public async Task MigrationUpgradeDoesNotCertifyLegacyGridOrAlterExistingSettings()
    {
        await using var database = await Database.CreateAsync(migrate: false);
        await using (var db = database.Factory.CreateDbContext())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260918120000_AddSolarObservationTimestamp");
            // Seed the historical schema without referencing columns introduced by the current tenant model.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Readings (Timestamp, BatterySoc, BatteryTemperature, BatteryVoltage, BatteryPower, BatteryCurrent,
                    SolarProduction, GridConsumption, LoadPower, DataSource, SolarObservedAt, SolarDeviceSn)
                VALUES ({Start.UtcDateTime}, 0, 0, 0, 0, 0, 0, -4300, 0, '', {Start.UtcDateTime}, 'selected');
                INSERT INTO AppSettings (Section, [Key], Value) VALUES ('Neighbor', 'untouched', 'preserved');
                """);
            await migrator.MigrateAsync();
        }
        await using var check = database.Factory.CreateDbContext();
        Assert.Empty(await check.ExportReadings.ToListAsync());
        Assert.Empty(await check.ExportPrices.ToListAsync());
        Assert.Equal(-4300, (await check.Readings.SingleAsync()).GridConsumption);
        Assert.Equal("preserved", (await check.AppSettings.SingleAsync()).Value);
        Assert.Contains("20260930160000_AddExportReadings", await check.Database.GetAppliedMigrationsAsync());
    }

    private static InverterData Poll(int watts, DateTimeOffset polledAt) => new()
    { GridConsumption = watts, GridObservedAt = Start, GridDeviceSn = "selected", Timestamp = polledAt };

    private sealed class Database(Factory factory) : IAsyncDisposable
    {
        public Factory Factory { get; } = factory;
        public ExportReadingStore Store { get; } = new(factory, new Clock());
        public static async Task<Database> CreateAsync(bool migrate = true)
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
            { InitialCatalog = "SolarExportTests_" + Guid.NewGuid().ToString("N") };
            var factory = new Factory(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options);
            var database = new Database(factory);
            if (migrate)
            {
                await using var db = factory.CreateDbContext();
                await db.Database.MigrateAsync();
            }
            return database;
        }
        public async ValueTask DisposeAsync()
        {
            await using var db = Factory.CreateDbContext();
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options, InstallationIds.Legacy);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }
    private sealed class RejectingFactory : IDbContextFactory<DeyeSolarDbContext>
    { public DeyeSolarDbContext CreateDbContext() => throw new InvalidOperationException("Database must not be opened."); }
}

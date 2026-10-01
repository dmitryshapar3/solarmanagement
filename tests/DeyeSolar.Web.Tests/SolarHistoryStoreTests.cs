using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Workers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tests;

public class SolarHistoryStoreTests
{
    private static readonly DateTime Start = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HistoryUsesMeasuredTimeAndLatestCorrectionWithoutWeightingRepeatedPolls()
    {
        var readings = Samples().AsQueryable();
        var rows = SolarHistoryStore.LatestMeasurements(readings, "selected", Start, Start.AddHours(1))
            .OrderBy(r => r.SolarObservedAt).ToArray();

        Assert.Equal(new[] { 2, 3, 10 }, rows.Select(r => r.Id));
        Assert.Equal(new[] { 2500, 0, 4000 }, rows.Select(r => r.SolarProduction));
        Assert.Equal(new[] { Start, Start.AddMinutes(5), Start.AddMinutes(10) }, rows.Select(r => r.SolarObservedAt!.Value));
    }

    [Fact]
    public void FilteringAndDeduplicationRemainInSql()
    {
        using var db = new DeyeSolarDbContext(new DbContextOptionsBuilder<DeyeSolarDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true;TrustServerCertificate=true").Options);
        var sql = SolarHistoryStore.LatestMeasurements(db.Readings.AsNoTracking(), "selected", Start, Start.AddHours(1))
            .Select(r => new { r.SolarObservedAt, r.SolarProduction }).ToQueryString();

        Assert.Contains("[SolarDeviceSn] =", sql);
        Assert.Contains("[SolarObservedAt] IS NOT NULL", sql);
        Assert.Contains("[SolarObservedAt] >=", sql);
        Assert.Contains("[SolarObservedAt] <", sql);
        Assert.Contains("GROUP BY", sql);
        Assert.Contains("MAX(", sql);
        Assert.DoesNotContain("AVG(", sql);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31 * 24 * 60 + 1)]
    public async Task RejectsInvalidOrUnboundedHistoryBeforeOpeningDatabase(int minutes)
    {
        var store = new SolarHistoryStore(new RejectingFactory());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadAsync("selected",
            new(Start), new(Start.AddMinutes(minutes)), default));
    }

    [Fact]
    public async Task MissingDeviceHasNoHistoricalReadingsAndDoesNotOpenDatabase()
    {
        var store = new SolarHistoryStore(new RejectingFactory());
        Assert.Empty(await store.ReadAsync(" ", new(Start), new(Start.AddDays(30).AddMinutes(10)), default));
    }

    [Fact]
    public void RetentionKeepsMonthAndBoundaryRegardlessOfDeviceOrMeasuredTimestamp()
    {
        var rows = RetentionSamples();
        var expired = PollingWorker.ExpiredReadings(rows.AsQueryable(), Start).Select(r => r.Id).ToArray();

        Assert.Equal(new[] { 21, 22 }, expired);
        Assert.Equal(7, rows.Length);
    }

    [SqlServerFact]
    public async Task SqlServerActualHistoryAndCurrentComparisonUseSolarGenerationRatherThanBatteryPower()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
        {
            InitialCatalog = "SolarPowerSourceTests_" + Guid.NewGuid().ToString("N")
        };
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(builder.ConnectionString).Options;
        var factory = new Factory(options);
        var clock = new FixedClock(new DateTimeOffset(Start.AddHours(4)));
        await using var owner = factory.CreateDbContext();
        try
        {
            await owner.Database.EnsureCreatedAsync();
            owner.AppSettings.Add(new AppSetting { Section = "Neighbor", Key = "unchanged", Value = "preserved" });
            await owner.SaveChangesAsync();
            var polling = new ExportReadingStore(factory, clock);
            for (var index = 0; index <= 12; index++)
            {
                var measured = new DateTimeOffset(Start.AddMinutes(index * 5));
                var battery = index % 2 == 0 ? -2742 : 2742;
                await polling.SavePollingAsync(new InverterData
                {
                    Timestamp = measured, SolarObservedAt = measured, SolarDeviceSn = "selected",
                    SolarProduction = 4100, BatteryPower = battery
                }, default);
                await polling.SavePollingAsync(new InverterData
                {
                    Timestamp = measured.AddHours(2), SolarObservedAt = measured.AddHours(2), SolarDeviceSn = "selected",
                    SolarProduction = 0, BatteryPower = battery
                }, default);
                await polling.SavePollingAsync(new InverterData
                {
                    Timestamp = measured, SolarObservedAt = measured, SolarDeviceSn = "neighbor",
                    SolarProduction = 9000, BatteryPower = -1111
                }, default);
            }

            await using (var check = factory.CreateDbContext())
            {
                var solarRows = await check.Readings.AsNoTracking().Where(row => row.SolarDeviceSn == "selected"
                    && row.SolarObservedAt < Start.AddHours(2)).OrderBy(row => row.SolarObservedAt).ToArrayAsync();
                Assert.Equal(13, solarRows.Length);
                Assert.All(solarRows, row => Assert.Equal(4100, row.SolarProduction));
                Assert.Equal(Enumerable.Range(0, 13).Select(index => index % 2 == 0 ? -2742 : 2742),
                    solarRows.Select(row => row.BatteryPower));
            }
            var before = await ReadStateAsync(factory);
            var historyStore = new SolarHistoryStore(factory);
            var deye = new Monitor<DeyeCloudOptions>(new() { DeviceSn = "selected" });
            var config = new Monitor<SolarEstimateOptions>(new()
                { DeyeSolarPowerIsPvDcConfirmed = true, DeyeConfirmedDeviceSn = "selected" });
            using var history = new SolarHistoryService(new EmptyWeather(), historyStore, config, deye, clock,
                NullLogger<SolarHistoryService>.Instance);
            var chart = await history.ReadAsync(SolarHistoryPeriod.Today, default);

            Assert.Null(chart.ActualError);
            Assert.Equal(4.1, chart.Points.Single(point => point.Timestamp == new DateTimeOffset(Start)).ActualKw!.Value, 10);
            Assert.Equal(0, chart.Points.Single(point => point.Timestamp == new DateTimeOffset(Start.AddHours(2))).ActualKw);
            var comparison = new SolarEstimateStore(factory, deye);
            Assert.Equal(4.1, (await comparison.FindActualAsync(new(Start), 0, clock.GetUtcNow(), default))!.PowerKw);
            Assert.Equal(0, (await comparison.FindActualAsync(new(Start.AddHours(2)), 0, clock.GetUtcNow(), default))!.PowerKw);
            var neighbor = await historyStore.ReadAsync("neighbor", new(Start), new(Start.AddHours(1)), default);
            Assert.Equal(12, neighbor.Count);
            Assert.All(neighbor, reading => Assert.Equal(9, reading.PowerKw));
            Assert.Equal(before, await ReadStateAsync(factory));
            await using var unchanged = factory.CreateDbContext();
            Assert.Equal("preserved", (await unchanged.AppSettings.SingleAsync()).Value);
            Assert.Empty(await unchanged.ExportReadings.ToArrayAsync());
        }
        finally
        {
            await owner.Database.EnsureDeletedAsync();
        }
    }

    [SqlServerFact]
    public async Task SqlServerHistoryDeduplicatesAndRetentionDeletesOnlyExpiredReadings()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
        {
            InitialCatalog = "SolarHistoryTests_" + Guid.NewGuid().ToString("N")
        };
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(builder.ConnectionString).Options;
        var factory = new Factory(options);
        await using var owner = new DeyeSolarDbContext(options);
        try
        {
            await owner.Database.EnsureCreatedAsync();
            var samples = Samples().Concat(RetentionSamples()).ToArray();
            foreach (var row in samples) row.Id = 0;
            owner.Readings.AddRange(samples);
            owner.AppSettings.Add(new AppSetting { Section = "Neighbor", Key = "unchanged", Value = "preserved" });
            owner.RuleRunLogs.Add(new RuleRunLog { Timestamp = Start.AddDays(-90), RuleName = "neighbor-rule" });
            await owner.SaveChangesAsync();
            var before = await ReadStateAsync(factory);

            var store = new SolarHistoryStore(factory);
            var actual = await store.ReadAsync("selected", new(Start), new(Start.AddHours(1)), default);
            Assert.Equal(new[] { 2.5, 0, 4.0 }, actual.Select(r => r.PowerKw));
            Assert.Equal(new[] { new DateTimeOffset(Start), new(Start.AddMinutes(5)), new(Start.AddMinutes(10)) },
                actual.Select(r => r.Timestamp));
            Assert.All(actual, r => Assert.Equal(SolarPowerBasis.PvDc, r.Basis));
            Assert.Empty(await store.ReadAsync("absent", new(Start), new(Start.AddHours(1)), default));
            Assert.Equal(9, Assert.Single(await store.ReadAsync("neighbor", new(Start), new(Start.AddHours(1)), default)).PowerKw);
            Assert.Equal(before, await ReadStateAsync(factory));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync("selected",
                new(Start), new(Start.AddHours(1)), cancellation.Token));
            Assert.Equal(before, await ReadStateAsync(factory));

            await using (var cleanup = await factory.CreateDbContextAsync())
                Assert.Equal(2, await PollingWorker.ExpiredReadings(cleanup.Readings, Start).ExecuteDeleteAsync());

            await using var check = await factory.CreateDbContextAsync();
            Assert.Equal(samples.Length - 2, await check.Readings.CountAsync());
            Assert.False(await check.Readings.AnyAsync(r => r.Timestamp < Start.AddDays(-31)));
            Assert.True(await check.Readings.AnyAsync(r => r.Timestamp == Start.AddDays(-31)));
            Assert.Equal("preserved", (await check.AppSettings.SingleAsync()).Value);
            Assert.Equal("neighbor-rule", (await check.RuleRunLogs.SingleAsync()).RuleName);
            var expectedRemaining = before.Where(r => r.Timestamp >= Start.AddDays(-31)).ToArray();
            Assert.Equal(expectedRemaining, await ReadStateAsync(factory));
            Assert.Equal(actual, await store.ReadAsync("selected", new(Start), new(Start.AddHours(1)), default));
        }
        finally
        {
            await owner.Database.EnsureDeletedAsync();
        }
    }

    private static Reading[] Samples() =>
    [
        Row(1, "selected", Start, 1000),
        Row(2, "selected", Start, 2500),
        Row(3, "selected", Start.AddMinutes(5), 0),
        Row(4, "neighbor", Start, 9000),
        Row(5, null, Start, 8000),
        Row(6, "selected", null, 7000),
        Row(7, "selected", Start.AddTicks(-1), 6000),
        Row(8, "selected", Start.AddHours(1), 5000),
        Row(9, "selected", Start.AddMinutes(5), -1),
        Row(10, "selected", Start.AddMinutes(10), 4000, Start.AddHours(3)),
        Row(11, "selected", Start.AddHours(2), 9000)
    ];

    private static Reading[] RetentionSamples() =>
    [
        Row(21, "selected", Start.AddDays(-1), 1000, Start.AddDays(-31).AddTicks(-1)),
        Row(22, "neighbor", Start.AddDays(-60), 2000, Start.AddDays(-32)),
        Row(23, "selected", Start.AddDays(-31), 3000, Start.AddDays(-31)),
        Row(24, "neighbor", Start.AddDays(-60), 4000, Start.AddDays(-30)),
        Row(25, "selected", null, 5000, Start.AddDays(-7)),
        Row(26, "neighbor", null, 6000, Start.AddDays(-4)),
        Row(27, "selected", Start.AddDays(-60), 7000, Start.AddDays(-1))
    ];

    private static Reading Row(int id, string? device, DateTime? observed, int watts, DateTime? polled = null) => new()
    {
        Id = id, Timestamp = polled ?? Start.AddMinutes(id), SolarObservedAt = observed,
        SolarDeviceSn = device, SolarProduction = watts, DataSource = "DeyeCloud"
    };

    private sealed record ReadingState(int Id, DateTime Timestamp, DateTime? Observed, string? Device, int Watts, int BatteryWatts);

    private static async Task<ReadingState[]> ReadStateAsync(Factory factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Readings.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new ReadingState(r.Id, r.Timestamp, r.SolarObservedAt, r.SolarDeviceSn, r.SolarProduction, r.BatteryPower)).ToArrayAsync();
    }

    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private sealed class RejectingFactory : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => throw new InvalidOperationException("Database must not be opened.");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class EmptyWeather : ISolarHistoryRadiationSource
    {
        public Task<IReadOnlyList<SolarWeatherSample>> ReadAsync(SolarEstimateOptions options,
            DateTimeOffset start, DateTimeOffset end, CancellationToken ct) => Task.FromResult<IReadOnlyList<SolarWeatherSample>>([]);
    }
}

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION")))
            Skip = "Set SOLAR_TEST_SQL_CONNECTION to run against an isolated SQL Server database.";
    }
}

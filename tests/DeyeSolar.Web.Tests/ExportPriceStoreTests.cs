using System.Data;
using System.Data.Common;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DeyeSolar.Web.Tests;

public class ExportPriceStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Start.AddDays(1); }

    [Fact]
    public async Task InvalidIntervalsPrecisionAndRetrievalBoundsNeverOpenDatabase()
    {
        var store = new ExportPriceStore(new RejectingFactory(), new Clock());
        var valid = Price(Start, 1.123456m);
        var invalid = new[]
        {
            valid with { Start = Start.AddMinutes(1), End = Start.AddMinutes(16) },
            valid with { End = Start.AddMinutes(30) },
            valid with { PricePlnPerMwh = 1.1234567m },
            valid with { PricePlnPerMwh = 1_000_000_000_000m },
            valid with { PricePlnPerMwh = -1_000_000_000_000m },
            Price(new DateTimeOffset(1999, 12, 31, 23, 45, 0, TimeSpan.Zero), 1m)
        };
        foreach (var item in invalid)
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync([valid, item], Start, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync([valid, valid with { PricePlnPerMwh = 2m }], Start, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync([valid], Start.AddDays(1).AddTicks(1), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync([valid],
            new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(Enumerable.Repeat(valid, 1001).ToArray(), Start, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadAsync(Start, Start, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadAsync(Start, Start.AddDays(368), default));
    }

    [SqlServerFact]
    public async Task SignedSixDecimalPricesRoundTripExactlyIncludingZeroAndColumnLimits()
    {
        await using var database = await Database.CreateAsync();
        var prices = new[]
        {
            Price(Start, -17.123456m), Price(Start.AddMinutes(15), 0m),
            Price(Start.AddMinutes(30), 699.130001m), Price(Start.AddMinutes(45), 999_999_999_999.999999m),
            Price(Start.AddHours(1), -999_999_999_999.999999m)
        };
        var retrievedAt = Start.AddHours(2);

        await database.Store.SaveAsync(prices, retrievedAt, default);
        await database.Store.SaveAsync(prices, retrievedAt, default);

        var read = await database.Store.ReadAsync(Start, Start.AddHours(2), default);
        Assert.Equal(prices, read);
        Assert.All(read, price => Assert.Equal(TimeSpan.Zero, price.Start.Offset));
        await using var check = database.Factory.CreateDbContext();
        var rows = await check.ExportPrices.AsNoTracking().OrderBy(row => row.StartUtc).ToArrayAsync();
        Assert.Equal(5, rows.Length);
        Assert.Equal(new[] { -17.123456m, 0m, 699.130001m, 999_999_999_999.999999m, -999_999_999_999.999999m },
            rows.Select(row => row.PricePlnPerMwh));
        Assert.All(rows, row => Assert.Equal(retrievedAt.UtcDateTime, row.RetrievedAtUtc));
    }

    [SqlServerFact]
    public async Task NewerCorrectionWinsAndOlderResponseCannotChangeIndependentQuarter()
    {
        await using var database = await Database.CreateAsync();
        await database.Store.SaveAsync([Price(Start, 100.111111m), Price(Start.AddMinutes(15), 222.222222m)], Start, default);
        await database.Store.SaveAsync([Price(Start, -50.654321m)], Start.AddHours(2), default);
        await database.Store.SaveAsync([Price(Start, 900m)], Start.AddHours(1), default);

        await using var check = database.Factory.CreateDbContext();
        var rows = await check.ExportPrices.AsNoTracking().OrderBy(row => row.StartUtc).ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.Equal(-50.654321m, rows[0].PricePlnPerMwh);
        Assert.Equal(Start.AddHours(2).UtcDateTime, rows[0].RetrievedAtUtc);
        Assert.Equal(222.222222m, rows[1].PricePlnPerMwh);
        Assert.Equal(Start.UtcDateTime, rows[1].RetrievedAtUtc);
    }

    [SqlServerFact]
    public async Task PrecisionRegressionDetectsInjectedTwoDecimalParameterScale()
    {
        await using var database = await Database.CreateAsync();
        var fault = new ReduceDecimalScale();
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>(database.Factory.Options)
            .AddInterceptors(fault).Options;
        var faultyStore = new ExportPriceStore(new Factory(options), new Clock());
        const decimal exact = 123.456789m;

        // Mutate only this test connection's parameter, never the application's source or schema.
        await faultyStore.SaveAsync([Price(Start, exact)], Start, default);
        var corrupted = Assert.Single(await database.Store.ReadAsync(Start, Start.AddMinutes(15), default));
        Assert.Equal(1, fault.Mutations);
        Assert.NotEqual(exact, corrupted.PricePlnPerMwh);

        await database.Store.SaveAsync([Price(Start, exact)], Start.AddHours(1), default);
        var corrected = Assert.Single(await database.Store.ReadAsync(Start, Start.AddMinutes(15), default));
        Assert.Equal(exact, corrected.PricePlnPerMwh);
    }

    [SqlServerFact]
    public async Task HalfOpenReadUsesUtcBoundsAndDoesNotIncludeNeighbourQuarters()
    {
        await using var database = await Database.CreateAsync();
        await database.Store.SaveAsync([
            Price(Start.AddMinutes(30), 4m), Price(Start.AddMinutes(-15), 1m),
            Price(Start.AddMinutes(15), 3m), Price(Start, 2m)], Start.AddHours(1), default);

        var result = await database.Store.ReadAsync(Start.ToOffset(TimeSpan.FromHours(2)),
            Start.AddMinutes(30).ToOffset(TimeSpan.FromHours(2)), default);

        Assert.Equal(new[] { Price(Start, 2m), Price(Start.AddMinutes(15), 3m) }, result);
        Assert.Empty(await database.Store.ReadAsync(Start.AddDays(1), Start.AddDays(2), default));
        await using var check = database.Factory.CreateDbContext();
        Assert.Equal(4, await check.ExportPrices.CountAsync());
        Assert.Equal(1m, (await check.ExportPrices.SingleAsync(row => row.StartUtc == Start.AddMinutes(-15).UtcDateTime)).PricePlnPerMwh);
        Assert.Equal(4m, (await check.ExportPrices.SingleAsync(row => row.StartUtc == Start.AddMinutes(30).UtcDateTime)).PricePlnPerMwh);
    }

    [SqlServerFact]
    public async Task InvalidBatchCannotApplyEarlierValidUpdateOrInsert()
    {
        await using var database = await Database.CreateAsync();
        await SeedNeighbours(database);

        await Assert.ThrowsAsync<ArgumentException>(() => database.Store.SaveAsync([
            Price(Start, 444m), Price(Start.AddMinutes(15), 555m),
            Price(Start.AddMinutes(30), 666m) with { End = Start.AddMinutes(60) }], Start.AddHours(2), default));

        await AssertNeighboursUnchanged(database);
    }

    [SqlServerFact]
    public async Task SqlFailureRollsBackAnEarlierUpdateAndInsertWithoutTouchingNeighbours()
    {
        await using var database = await Database.CreateAsync();
        await SeedNeighbours(database);
        await using (var schema = database.Factory.CreateDbContext())
            await schema.Database.ExecuteSqlRawAsync("ALTER TABLE ExportPrices ADD CONSTRAINT CK_TestPriceBatchFailure CHECK (PricePlnPerMwh <> 777777);");

        await Assert.ThrowsAsync<SqlException>(() => database.Store.SaveAsync([
            Price(Start, 444m), Price(Start.AddMinutes(15), 555m),
            Price(Start.AddMinutes(30), 777777m)], Start.AddHours(2), default));

        await AssertNeighboursUnchanged(database);
    }

    [SqlServerFact]
    public async Task ConcurrentOverlappingBatchesAreIdempotentAndKeepNewestRetrieval()
    {
        await using var database = await Database.CreateAsync();
        await database.Store.SaveAsync([Price(Start.AddHours(1), 321.000001m)], Start, default);
        var writes = Enumerable.Range(1, 8).Select(index => database.Store.SaveAsync([
            Price(Start.AddMinutes(15), -index / 1_000_000m), Price(Start, index / 1_000_000m)],
            Start.AddMinutes(index), default));

        await Task.WhenAll(writes);
        await database.Store.SaveAsync([Price(Start, 0.000008m), Price(Start, 0.000008m), Price(Start.AddMinutes(15), -0.000008m)],
            Start.AddMinutes(8), default);

        await using var check = database.Factory.CreateDbContext();
        var rows = await check.ExportPrices.AsNoTracking().OrderBy(row => row.StartUtc).ToArrayAsync();
        Assert.Equal(3, rows.Length);
        Assert.Equal(new[] { 0.000008m, -0.000008m, 321.000001m }, rows.Select(row => row.PricePlnPerMwh));
        Assert.Equal(Start.AddMinutes(8).UtcDateTime, rows[0].RetrievedAtUtc);
        Assert.Equal(Start.AddMinutes(8).UtcDateTime, rows[1].RetrievedAtUtc);
        Assert.Equal(Start.UtcDateTime, rows[2].RetrievedAtUtc);
    }

    [SqlServerFact]
    public async Task CancellationAfterFirstSqlWriteRollsBackTheBatch()
    {
        await using var database = await Database.CreateAsync();
        await SeedNeighbours(database);
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelAfterFirstPriceWrite(cancellation);
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>(database.Factory.Options)
            .AddInterceptors(interceptor).Options;
        var store = new ExportPriceStore(new Factory(options), new Clock());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync([
            Price(Start, 444m), Price(Start.AddMinutes(15), 555m)], Start.AddHours(2), cancellation.Token));

        Assert.Equal(1, interceptor.CompletedWrites);
        await AssertNeighboursUnchanged(database);
    }

    private static ExportPriceInterval Price(DateTimeOffset start, decimal value) => new(start, start.AddMinutes(15), value);

    private static async Task SeedNeighbours(Database database)
    {
        await database.Store.SaveAsync([Price(Start, 111.123456m), Price(Start.AddHours(1), 222.654321m)], Start, default);
        await using var db = database.Factory.CreateDbContext();
        db.AppSettings.Add(new AppSetting { Section = "Neighbor", Key = "untouched", Value = "preserved" });
        db.ExportReadings.Add(new ExportReading { DeviceSn = "unrelated-device", ObservedAt = Start.UtcDateTime, PolledAt = Start.UtcDateTime, GridPowerWatts = -1234 });
        await db.SaveChangesAsync();
    }

    private static async Task AssertNeighboursUnchanged(Database database)
    {
        // A fresh context observes committed state after disposal of the failed transaction.
        await using var check = database.Factory.CreateDbContext();
        var rows = await check.ExportPrices.AsNoTracking().OrderBy(row => row.StartUtc).ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { Start.UtcDateTime, Start.AddHours(1).UtcDateTime }, rows.Select(row => row.StartUtc));
        Assert.Equal(new[] { 111.123456m, 222.654321m }, rows.Select(row => row.PricePlnPerMwh));
        Assert.All(rows, row => Assert.Equal(Start.UtcDateTime, row.RetrievedAtUtc));
        var setting = Assert.Single(await check.AppSettings.AsNoTracking().ToArrayAsync());
        Assert.Equal("Neighbor", setting.Section);
        Assert.Equal("untouched", setting.Key);
        Assert.Equal("preserved", setting.Value);
        var reading = Assert.Single(await check.ExportReadings.AsNoTracking().ToArrayAsync());
        Assert.Equal("unrelated-device", reading.DeviceSn);
        Assert.Equal(-1234, reading.GridPowerWatts);
        Assert.Equal(Start.UtcDateTime, reading.ObservedAt);
        Assert.Equal(Start.UtcDateTime, reading.PolledAt);
    }

    private sealed class CancelAfterFirstPriceWrite(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public int CompletedWrites { get; private set; }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE [ExportPrices]", StringComparison.Ordinal) && ++CompletedWrites == 1)
                cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ReduceDecimalScale : DbCommandInterceptor
    {
        public int Mutations { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var parameter in command.Parameters.OfType<SqlParameter>().Where(parameter => parameter.SqlDbType == SqlDbType.Decimal))
            {
                parameter.Scale = 2;
                Mutations++;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Database(Factory factory) : IAsyncDisposable
    {
        public Factory Factory { get; } = factory;
        public ExportPriceStore Store { get; } = new(factory, new Clock());

        public static async Task<Database> CreateAsync()
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
            { InitialCatalog = "SolarPriceTests_" + Guid.NewGuid().ToString("N") };
            var factory = new Factory(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options);
            var database = new Database(factory);
            await using var db = factory.CreateDbContext();
            await db.Database.MigrateAsync();
            await TestInstallation.EnsureAsync(db);
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
        public DbContextOptions<DeyeSolarDbContext> Options { get; } = options;
        public DeyeSolarDbContext CreateDbContext() => new(Options, TestInstallation.Id);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }

    private sealed class RejectingFactory : IDbContextFactory<DeyeSolarDbContext>
    { public DeyeSolarDbContext CreateDbContext() => throw new InvalidOperationException("Database must not be opened."); }
}

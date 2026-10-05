using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DeyeSolar.Web.Tests;

public class SolarHistoryQualityMigrationTests
{
    private static readonly DateTime Start = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);
    private const string QualityMigration = "20261005110000_PersistHistoricalMeasurementQuality";

    [SqlServerFact]
    public async Task RestoresProvenArchivedPvAndItsHourlyLineWithoutPromotingOtherOrNewInvalidMeasurements()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync("SolarPvQualityRepair", SqlTestSchema.None);
        await using var db = database.Factory.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(QualityMigration);
        await TestInstallation.EnsureAsync(db);
        var measured = Enumerable.Range(0, 13).Select(i => Row(Start.AddMinutes(i * 5), i * 500)).ToArray();
        var olderProvider = Row(Start, 0);
        olderProvider.DataSource = "DeyeCloud";
        var invalid = new[]
        {
            Row(Start, -1), Row(null, 1500), Row(Start, 1500, null), Row(Start, 1500, "   "),
            Row(Start.AddHours(3), 1500), Row(new DateTime(1999, 12, 31), 1500), Row(Start, 1500)
        };
        invalid[^1].DataSource = "Unknown";
        db.Readings.AddRange(measured.Concat([olderProvider]).Concat(invalid));
        db.AppSettings.Add(new() { Section = "Neighbor", Key = "preserve", Value = "untouched" });
        await db.SaveChangesAsync();

        // A good non-PV metric also establishes the global quality-contract boundary.
        var boundary = Row(Start.AddHours(1), 1000);
        boundary.BatteryPowerValid = true;
        db.Readings.Add(boundary);
        await db.SaveChangesAsync();
        var modernInvalid = Row(Start.AddHours(1), 2000);
        db.Readings.Add(modernInvalid);
        await db.SaveChangesAsync();
        var before = await db.Readings.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.Timestamp, r.SolarObservedAt, r.SolarDeviceSn, r.SolarProduction, r.BatteryPower, r.DataSource }).ToArrayAsync();
        var store = new SolarHistoryStore(database.Factory);
        Assert.Empty(await store.ReadAsync("selected", new(Start), new(Start.AddHours(1).AddMinutes(1)), default));

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        var restored = await db.Readings.AsNoTracking().OrderBy(r => r.Id).ToArrayAsync();
        Assert.All(restored.Where(r => measured.Select(p => p.Id).Append(olderProvider.Id).Contains(r.Id)), r => Assert.True(r.SolarPowerValid));
        Assert.All(restored.Where(r => invalid.Select(p => p.Id).Append(boundary.Id).Append(modernInvalid.Id).Contains(r.Id)), r => Assert.False(r.SolarPowerValid));
        Assert.All(restored, r =>
        {
            Assert.False(r.BatteryTemperatureValid); Assert.False(r.BatteryVoltageValid);
            Assert.False(r.BatteryCurrentValid); Assert.False(r.LoadPowerValid); Assert.False(r.GridPowerValid);
            Assert.Equal(r.Id == boundary.Id, r.BatteryPowerValid);
        });
        Assert.Equal(before, await db.Readings.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.Timestamp, r.SolarObservedAt, r.SolarDeviceSn, r.SolarProduction, r.BatteryPower, r.DataSource }).ToArrayAsync());
        Assert.Equal("untouched", (await db.AppSettings.SingleAsync()).Value);
        var samples = await store.ReadAsync("selected", new(Start), new(Start.AddHours(1).AddMinutes(1)), default);
        Assert.Equal(13, samples.Count);
        Assert.Equal(3, SolarHistoryAggregation.MeanPower(samples, new(Start)));
        await migrator.MigrateAsync();
        Assert.Equal(restored.Count(r => r.SolarPowerValid), await db.Readings.CountAsync(r => r.SolarPowerValid));

        var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(QualityMigration));
        Assert.Equal(51000, error.Number);
        Assert.Equal(13, (await store.ReadAsync("selected", new(Start), new(Start.AddHours(1).AddMinutes(1)), default)).Count);
    }

    [SqlServerFact]
    public async Task BoundaryIsGlobalAcrossInstallationsAndAnUnprovenCutoverCannotPromoteMeasurements()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync("SolarPvQualityBoundary", SqlTestSchema.None);
        await using var db = database.Factory.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(QualityMigration);
        await TestInstallation.EnsureAsync(db);
        var archive = Row(Start, 0);
        db.Readings.Add(archive);
        await db.SaveChangesAsync();
        // Without an explicit current-contract row there is no write-order fence.
        // Do not reinterpret potentially newer invalid records in that case.
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.False((await db.Readings.SingleAsync()).SolarPowerValid);

        // Exercise the SQL migration against a second isolated pre-repair database.
        await using var mixed = await SqlServerTestDatabase.CreateAsync("SolarPvQualityTenants", SqlTestSchema.None);
        await using var owner = mixed.Factory.CreateDbContext();
        var mixedMigrator = owner.GetService<IMigrator>();
        await mixedMigrator.MigrateAsync(QualityMigration);
        await TestInstallation.EnsureAsync(owner);
        owner.Installations.Add(new() { Id = "neighbor", CreatedAt = new DateTimeOffset(Start) });
        var old = Row(Start, 1000);
        owner.Readings.Add(old);
        await owner.SaveChangesAsync();
        await using (var neighbor = new DeyeSolarDbContext(mixed.Options, "neighbor"))
        {
            var firstModern = Row(Start, 2000); firstModern.SolarPowerValid = true;
            neighbor.Readings.Add(firstModern);
            await neighbor.SaveChangesAsync();
        }
        var newInvalid = Row(Start, 3000);
        owner.Readings.Add(newInvalid);
        await owner.SaveChangesAsync();
        await mixedMigrator.MigrateAsync();
        owner.ChangeTracker.Clear();
        Assert.True((await owner.Readings.SingleAsync(r => r.Id == old.Id)).SolarPowerValid);
        Assert.False((await owner.Readings.SingleAsync(r => r.Id == newInvalid.Id)).SolarPowerValid);
        Assert.Equal(2, await owner.Readings.IgnoreQueryFilters().CountAsync(r => r.SolarPowerValid));
    }

    private static Reading Row(DateTime? observed, int watts, string? device = "selected") => new()
    {
        Timestamp = Start.AddHours(2), SolarObservedAt = observed, SolarDeviceSn = device,
        SolarProduction = watts, DataSource = "Integration", BatteryPower = -2700
    };
}

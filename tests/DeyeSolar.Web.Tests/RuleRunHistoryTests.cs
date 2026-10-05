using DeyeSolar.Domain.Models;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeyeSolar.Web.Tests;

public sealed class RuleRunHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task RecordingUsesOneClockSnapshotAndRetainsTheCompleteQueryableSevenDayBoundary()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync("SolarRuleRetention", seed: TestInstallation.EnsureAsync);
        var cutoff = HistoryQueryPolicy.Cutoff(Now, HistoryQueryPolicy.MaximumHours);
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.RuleRunLogs.AddRange(
                new RuleRunLog { RuleName = "expired", Timestamp = cutoff.AddTicks(-1) },
                new RuleRunLog { RuleName = "boundary", Timestamp = cutoff },
                new RuleRunLog { RuleName = "four-days-old", Timestamp = Now.AddDays(-4).UtcDateTime },
                new RuleRunLog { RuleName = "updated", Timestamp = Now.AddMinutes(-1).UtcDateTime,
                    Action = "NO_CHANGE", ConditionKey = "no-change:off:soc-below-threshold" });
            var readingCutoff = Now.AddDays(-31).UtcDateTime;
            seed.Readings.AddRange(new Reading { Timestamp = readingCutoff.AddTicks(-1) }, new Reading { Timestamp = readingCutoff });
            await seed.SaveChangesAsync();
        }

        var clock = new Clock();
        var history = new RuleRunHistory(database.Factory, NullLogger.Instance, clock);
        var data = ConfirmedInverterReading.Create(new() { BatterySoc = 50, Timestamp = Now });
        var decision = new RuleEvaluator().Decide(data, new TriggerRule { SocTurnOnThreshold = 80 }, Now);
        await history.RecordAsync(data, [new("updated", decision, false, null), new("inserted", decision, false, null)], default);
        Assert.Equal(1, clock.Calls);
        await using (var check = database.Factory.CreateDbContext())
        {
            var rows = await check.RuleRunLogs.ToArrayAsync();
            Assert.Equal(4, rows.Length);
            Assert.DoesNotContain(rows, row => row.RuleName == "expired");
            Assert.Contains(rows, row => row.RuleName == "boundary" && row.Timestamp == cutoff);
            Assert.Contains(rows, row => row.RuleName == "four-days-old");
            Assert.All(rows.Where(row => row.RuleName is "updated" or "inserted"), row => Assert.Equal(Now.UtcDateTime, row.Timestamp));
            var maximumQuery = HistoryQueryPolicy.Range(Now, HistoryQueryPolicy.MaximumHours + 1);
            Assert.Equal(cutoff, maximumQuery.Cutoff);
            Assert.Equal(rows.Length, await HistoryQueryPolicy.Runs(check.RuleRunLogs, maximumQuery).CountAsync());
        }

        await history.CleanupAsync(default);
        Assert.Equal(2, clock.Calls);
        await using var retained = database.Factory.CreateDbContext();
        Assert.Equal(Now.AddDays(-31).UtcDateTime, (await retained.Readings.SingleAsync()).Timestamp);
    }

    [SqlServerFact]
    public async Task SolarAverageRequiresBothMetricQualitiesAndPreservesMeasuredZero()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync("SolarRuleAverage", seed: TestInstallation.EnsureAsync);
        var now = Now.UtcDateTime;
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.Readings.AddRange(Sample(3000, true, true), Sample(0, true, true), Sample(9000, false, true), Sample(8000, true, false));
            await seed.SaveChangesAsync();
        }
        var context = await new RuleRunHistory(database.Factory, NullLogger.Instance, new Clock()).BuildContextAsync(now, "selected", default);
        Assert.Equal(1500, context.AverageSolarProductionWatts);

        Reading Sample(int watts, bool solarValid, bool socValid) => new()
        {
            Timestamp = now, SolarObservedAt = now, SolarDeviceSn = "selected", SolarProduction = watts,
            SolarPowerValid = solarValid, BatterySoc = 90, BatterySocValid = socValid
        };
    }

    [SqlServerFact]
    public async Task RepeatedTimeWindowOffRetainsMeasuredZeroButClearsUnavailableSoc()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync("SolarRuleHistory", seed: TestInstallation.EnsureAsync);
        var now = Now;
        var localTime = TimeOnly.FromDateTime(now.UtcDateTime);
        var inactiveTime = localTime.AddHours(1);
        var rule = new TriggerRule { Id = 1, Name = "window-rule", CurrentState = true,
            SocTurnOnThreshold = 20, ActiveFrom = inactiveTime, ActiveTo = inactiveTime };
        var history = new RuleRunHistory(database.Factory, NullLogger.Instance, new Clock());
        var measuredZero = ConfirmedInverterReading.Create(new() { BatterySoc = 0, Timestamp = now, SolarObservedAt = now });
        var validDecision = new RuleEvaluator().Decide(measuredZero, rule, now);
        await history.RecordAsync(measuredZero, [new(rule.Name, validDecision, true, null)], default);
        await using (var check = database.Factory.CreateDbContext())
        {
            var zero = await check.RuleRunLogs.SingleAsync();
            Assert.Equal(0, zero.BatterySoc);
            Assert.Equal(0, zero.SolarProduction);
            Assert.Equal(0, zero.BatteryPower);
        }

        var unavailable = measuredZero with { BatterySoc = 87, BatterySocValid = false, Telemetry = null };
        var unknownDecision = new RuleEvaluator().Decide(unavailable, rule, now);
        await history.RecordAsync(unavailable, [new(rule.Name, unknownDecision, true, null)], default);
        await using var final = database.Factory.CreateDbContext();
        var row = await final.RuleRunLogs.SingleAsync();
        Assert.Equal("OFF", row.Action);
        Assert.Equal("action:off:time-window", row.ConditionKey);
        Assert.Null(row.BatterySoc);
        Assert.Null(row.SolarProduction);
        Assert.Null(row.BatteryPower);
        Assert.Null(row.ToDto().BatterySoc);
        Assert.Equal(Now.UtcDateTime, row.Timestamp);
    }

    [SqlServerFact]
    public async Task ForwardMigrationPreservesExistingNumericArchivesAndAllowsUnknownSoc()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync("SolarRuleSocMigration", SqlTestSchema.None);
        await using var db = database.Factory.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261004225742_RemoveLegacyInstallationCompatibility");
        await TestInstallation.EnsureAsync(db);
        db.RuleRunLogs.Add(new() { RuleName = "numeric-archive", Timestamp = DateTime.UtcNow, BatterySoc = 0, SolarProduction = 0, BatteryPower = 0 });
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT [Readings] ([InstallationId], [Timestamp], [BatterySoc], [BatterySocValid], [BatteryTemperature], [BatteryVoltage],
                [BatteryPower], [BatteryCurrent], [SolarProduction], [GridConsumption], [LoadPower], [DataSource], [ConfigurationRevision], [RuntimeGeneration])
            VALUES ({TestInstallation.Id}, SYSUTCDATETIME(), 50, 1, 20, 48, 0, 0, 1000, 0, 0, 'historical-known-numeric', 0, 0);
            """);
        await db.SaveChangesAsync();
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(0, (await db.RuleRunLogs.SingleAsync()).BatterySoc);
        var archived = await db.Readings.SingleAsync();
        Assert.True(archived.BatterySocValid);
        Assert.False(archived.SolarPowerValid);
        Assert.False(archived.BatteryPowerValid);
        Assert.False(archived.GridPowerValid);
        Assert.False(archived.LoadPowerValid);
        Assert.False(archived.BatteryVoltageValid);
        Assert.False(archived.BatteryTemperatureValid);
        Assert.False(archived.BatteryCurrentValid);
        db.RuleRunLogs.Add(new() { RuleName = "unknown", Timestamp = DateTime.UtcNow, BatterySoc = null });
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(
            () => migrator.MigrateAsync("20261004225742_RemoveLegacyInstallationCompatibility"));
        Assert.Equal(51000, error.Number);
        db.ChangeTracker.Clear();
        Assert.Null((await db.RuleRunLogs.SingleAsync(row => row.RuleName == "unknown")).BatterySoc);
    }

    private sealed class Clock : TimeProvider
    {
        public int Calls { get; private set; }
        public override DateTimeOffset GetUtcNow() { Calls++; return Now; }
    }
}

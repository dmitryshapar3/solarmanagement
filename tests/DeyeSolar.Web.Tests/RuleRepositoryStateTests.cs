using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

public class RuleRepositoryStateTests
{
    [SqlServerFact]
    public async Task DisabledInstallationRejectsStaleRuleCreateUpdateAndDelete()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = new RuleRepository(fixture.Factory("site-a"));
        var edited = (await repository.GetByIdAsync(fixture.TargetId, default))!;
        var originalName = edited.Name;
        edited.Name = "Late edit";
        await using (var db = fixture.Factory("site-a").CreateDbContext())
        {
            (await db.Installations.SingleAsync(installation => installation.Id == "site-a")).IsEnabled = false;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<DeyeSolar.Web.Auth.InstallationAccessException>(() => repository.CreateAsync(new() { EntityId = "late-target", Name = "Late create" }, default));
        await Assert.ThrowsAsync<DeyeSolar.Web.Auth.InstallationAccessException>(() => repository.UpdateAsync(edited, default));
        await Assert.ThrowsAsync<DeyeSolar.Web.Auth.InstallationAccessException>(() => repository.DeleteAsync(edited.Id, default));
        Assert.Equal(originalName, (await repository.GetByIdAsync(edited.Id, default))!.Name);
    }

    [SqlServerFact]
    public async Task StaleEditorCannotOverwriteAnotherEditorsConfigurationButRuntimeUpdatesDoNotConflict()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = new RuleRepository(fixture.Factory("site-a"));
        var first = (await repository.GetByIdAsync(fixture.TargetId, default))!;
        var stale = (await repository.GetByIdAsync(fixture.TargetId, default))!;
        first.Name = "First saved edit";
        await repository.UpdateAsync(first, default);
        stale.SocTurnOnThreshold = 99;
        await Assert.ThrowsAsync<RuleConfigurationConflictException>(() => repository.UpdateAsync(stale, default));
        var current = (await repository.GetByIdAsync(fixture.TargetId, default))!;
        Assert.Equal("First saved edit", current.Name);
        Assert.Equal(70, current.SocTurnOnThreshold);
        await repository.RecordEvaluationAsync(current.Id, DateTime.UtcNow, default);
        current.Name = "Saved after evaluation";
        await repository.UpdateAsync(current, default);
        Assert.Equal("Saved after evaluation", (await repository.GetByIdAsync(current.Id, default))!.Name);
    }
    [SqlServerFact]
    public async Task ChangingPhysicalTargetDoesNotInheritThePreviousTargetsStateOrCooldown()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = new RuleRepository(fixture.Factory("site-a"));
        await using (var db = fixture.Factory("site-a").CreateDbContext())
        {
            var rule = await db.TriggerRules.SingleAsync(r => r.Id == fixture.TargetId);
            rule.CurrentState = true;
            rule.CurrentStateChangedAt = DateTime.UtcNow;
            rule.LastEvaluated = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var edited = (await repository.GetByIdAsync(fixture.TargetId, default))!;
        edited.EntityId = "new-physical-socket";
        await repository.UpdateAsync(edited, default);
        var saved = (await repository.GetByIdAsync(fixture.TargetId, default))!;
        Assert.False(saved.CurrentState);
        Assert.Null(saved.CurrentStateChangedAt);
        Assert.Null(saved.LastEvaluated);
    }

    [SqlServerFact]
    public async Task DuplicateTargetAdmissionRejectsCreateAndEnableButAllowsDisabledDrafts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = new RuleRepository(fixture.Factory("site-a"));
        var duplicate = new TriggerRule { EntityId = "socket-one", Name = "Duplicate" };
        Assert.Contains("already has an enabled automation rule", (await Assert.ThrowsAsync<ArgumentException>(
            () => repository.CreateAsync(duplicate, default))).Message);
        duplicate.Enabled = false;
        await repository.CreateAsync(duplicate, default);
        duplicate.Enabled = true;
        await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateAsync(duplicate, default));
        Assert.False((await repository.GetByIdAsync(duplicate.Id, default))!.Enabled);
    }

    [SqlServerFact]
    public async Task ConcurrentDuplicateAdmissionHasExactlyOneWinner()
    {
        await using var fixture = await Fixture.CreateAsync();
        async Task<bool> AdmitAsync(string name)
        {
            try
            {
                await new RuleRepository(fixture.Factory("site-a")).CreateAsync(new() { EntityId = "contended-target", Name = name }, default);
                return true;
            }
            catch (ArgumentException) { return false; }
        }
        Assert.Single(await Task.WhenAll(AdmitAsync("First"), AdmitAsync("Second")), winner => winner);
    }
    [SqlServerFact]
    public async Task StaleConfigurationSavePreservesAcknowledgedStateAndEvaluationAcrossIndependentRules()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = new RuleRepository(fixture.Factory("site-a"));
        var stale = (await repository.GetByIdAsync(fixture.TargetId, default))!;
        var neighbours = await fixture.NeighboursAsync();
        var acknowledgedAt = new DateTime(2026, 10, 4, 12, 30, 0, DateTimeKind.Utc);
        var evaluatedAt = acknowledgedAt.AddSeconds(3);
        await using (var ack = fixture.Factory("site-a").CreateDbContext())
        {
            var current = await ack.TriggerRules.SingleAsync(rule => rule.Id == fixture.TargetId);
            current.CurrentState = true;
            current.CurrentStateChangedAt = acknowledgedAt;
            current.LastEvaluated = evaluatedAt;
            await ack.SaveChangesAsync();
        }
        stale.Name = "Edited after acknowledgement";
        stale.SocTurnOnThreshold = 91;
        stale.Enabled = false;
        await repository.UpdateAsync(stale, default);

        await using var check = fixture.Factory("site-a").CreateDbContext();
        var saved = await check.TriggerRules.AsNoTracking().SingleAsync(rule => rule.Id == fixture.TargetId);
        Assert.Equal("Edited after acknowledgement", saved.Name);
        Assert.Equal(91, saved.SocTurnOnThreshold);
        Assert.False(saved.Enabled);
        Assert.True(saved.CurrentState);
        Assert.Equal(acknowledgedAt, saved.CurrentStateChangedAt);
        Assert.Equal(evaluatedAt, saved.LastEvaluated);
        Assert.True(stale.CurrentState);
        Assert.Equal(acknowledgedAt, stale.CurrentStateChangedAt);
        Assert.Equal(evaluatedAt, stale.LastEvaluated);
        Assert.Equal(neighbours, await fixture.NeighboursAsync());
    }

    [SqlServerFact]
    public async Task EvaluationBookkeepingCannotRestoreStaleConfigurationOrAcknowledgedStateAndIsTenantBound()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = new RuleRepository(fixture.Factory("site-a"));
        var neighbours = await fixture.NeighboursAsync();
        var acknowledgedAt = new DateTime(2026, 10, 4, 12, 30, 0, DateTimeKind.Utc);
        await using (var current = fixture.Factory("site-a").CreateDbContext())
        {
            var rule = await current.TriggerRules.SingleAsync(rule => rule.Id == fixture.TargetId);
            rule.Name = "Current editor configuration";
            rule.SocTurnOnThreshold = 92;
            rule.Enabled = false;
            rule.CurrentState = true;
            rule.CurrentStateChangedAt = acknowledgedAt;
            await current.SaveChangesAsync();
        }
        var evaluatedAt = acknowledgedAt.AddSeconds(7);
        await repository.RecordEvaluationAsync(fixture.TargetId, evaluatedAt, default);
        await repository.RecordEvaluationAsync(fixture.TargetId, evaluatedAt, default);
        await repository.RecordEvaluationAsync(fixture.TargetId, evaluatedAt.AddSeconds(-5), default);
        await new RuleRepository(fixture.Factory("site-b")).RecordEvaluationAsync(fixture.TargetId, evaluatedAt.AddDays(1), default);
        await using var check = fixture.Factory("site-a").CreateDbContext();
        var saved = await check.TriggerRules.AsNoTracking().SingleAsync(rule => rule.Id == fixture.TargetId);
        Assert.Equal("Current editor configuration", saved.Name);
        Assert.Equal(92, saved.SocTurnOnThreshold);
        Assert.False(saved.Enabled);
        Assert.True(saved.CurrentState);
        Assert.Equal(acknowledgedAt, saved.CurrentStateChangedAt);
        Assert.Equal(evaluatedAt, saved.LastEvaluated);
        Assert.Equal(neighbours, await fixture.NeighboursAsync());
    }

    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options, string installation) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options, installation);
    }

    private sealed class Fixture(DbContextOptions<DeyeSolarDbContext> options) : IAsyncDisposable
    {
        public int TargetId { get; private set; }
        public IDbContextFactory<DeyeSolarDbContext> Factory(string installation) => new Factory(options, installation);
        public async Task<string> NeighboursAsync()
        {
            await using var db = Factory("site-a").CreateDbContext();
            var rows = await db.TriggerRules.IgnoreQueryFilters().AsNoTracking().Where(rule => rule.Id != TargetId).OrderBy(rule => rule.Id).ToListAsync();
            return System.Text.Json.JsonSerializer.Serialize(rows);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
            { InitialCatalog = "RuleStateTests_" + Guid.NewGuid().ToString("N") };
            var fixture = new Fixture(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options);
            await using var db = fixture.Factory("site-a").CreateDbContext();
            await db.Database.MigrateAsync();
            db.Installations.AddRange(new Installation { Id = "site-a", CreatedAt = DateTimeOffset.UtcNow }, new Installation { Id = "site-b", CreatedAt = DateTimeOffset.UtcNow });
            var target = new TriggerRule { Name = "Target", EntityId = "socket-one", SocTurnOnThreshold = 70 };
            db.TriggerRules.AddRange(target, new TriggerRule
            {
                Name = "Independent neighbour",
                EntityId = "socket-two",
                CurrentState = true,
                CurrentStateChangedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
                LastEvaluated = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
            fixture.TargetId = target.Id;
            await using var foreign = fixture.Factory("site-b").CreateDbContext();
            foreign.TriggerRules.Add(new() { Name = "Foreign same socket", EntityId = "socket-one", Enabled = false, SocTurnOnThreshold = 11 });
            await foreign.SaveChangesAsync();
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await using var db = Factory("site-a").CreateDbContext();
            await db.Database.EnsureDeletedAsync();
        }
    }
}

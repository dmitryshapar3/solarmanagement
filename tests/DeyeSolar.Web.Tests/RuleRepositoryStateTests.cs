using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

public class RuleRepositoryStateTests
{
    [SqlServerFact]
    public async Task DirectConfigurationWritesNormalizeDraftsAndCannotPersistInvalidThresholds()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = new RuleRepository(fixture.Factory("site-a"));
        var draft = await repository.CreateAsync(new()
        {
            Name = "Unselected draft", EntityId = "", Enabled = true,
            SocTurnOnThreshold = 80, UseSeparateSocTurnOffThreshold = false, SocTurnOffThreshold = 101,
            UseSolarProductionThreshold = true, MinAverageSolarProductionWatts = 0
        }, default);
        var saved = (await repository.GetByIdAsync(draft.Id, default))!;
        Assert.False(saved.Enabled);
        Assert.Equal(80, saved.SocTurnOffThreshold);
        Assert.Equal(3000, saved.MinAverageSolarProductionWatts);
        saved.SocTurnOnThreshold = 101;
        await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateAsync(saved, default));
        Assert.Equal(80, (await repository.GetByIdAsync(draft.Id, default))!.SocTurnOnThreshold);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateAsync(new()
        { Name = "", Enabled = false, EntityId = fixture.ReplacementSocket.ToString("D") }, default));
    }

    [SqlServerFact]
    public async Task MissingAndStaleVersionsCannotUpdateOrDeleteAndRuntimeBookkeepingKeepsVersionValid()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = new RuleRepository(fixture.Factory("site-a"));
        var current = (await repository.GetByIdAsync(fixture.TargetId, default))!;
        var version = current.ConfigurationVersion!;
        var name = current.Name;
        current.ConfigurationVersion = null;
        current.Name = "Unconditional edit";
        await Assert.ThrowsAsync<RuleConfigurationPreconditionRequiredException>(() => repository.UpdateAsync(current, default));
        await Assert.ThrowsAsync<RuleConfigurationPreconditionRequiredException>(() => repository.DeleteAsync(current.Id, "", default));
        Assert.Equal(name, (await repository.GetByIdAsync(current.Id, default))!.Name);
        current = (await repository.GetByIdAsync(current.Id, default))!;
        current.Name = "Concurrent editor saved";
        await repository.UpdateAsync(current, default);
        await Assert.ThrowsAsync<RuleConfigurationConflictException>(() => repository.DeleteAsync(current.Id, version, default));
        Assert.Equal("Concurrent editor saved", (await repository.GetByIdAsync(current.Id, default))!.Name);
        var latest = (await repository.GetByIdAsync(current.Id, default))!;
        await repository.RecordEvaluationAsync(latest.Id, DateTime.UtcNow, default);
        await repository.DeleteAsync(latest.Id, latest.ConfigurationVersion!, default);
        Assert.Null(await repository.GetByIdAsync(latest.Id, default));
        await Assert.ThrowsAsync<RuleConfigurationPreconditionRequiredException>(() => repository.DeleteAsync(latest.Id, "", default));
    }

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
        await Assert.ThrowsAsync<DeyeSolar.Web.Auth.InstallationAccessException>(() => repository.DeleteAsync(edited.Id, edited.ConfigurationVersion!, default));
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
        edited.EntityId = fixture.ReplacementSocket.ToString("D");
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
        var duplicate = new TriggerRule { EntityId = fixture.TargetSocket.ToString("D"), Name = "Duplicate" };
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
                await new RuleRepository(fixture.Factory("site-a")).CreateAsync(new() { EntityId = fixture.ContendedSocket.ToString("D"), Name = name }, default);
                return true;
            }
            catch (ArgumentException) { return false; }
        }
        Assert.Single(await Task.WhenAll(AdmitAsync("First"), AdmitAsync("Second")), winner => winner);
    }
    [SqlServerFact]
    public async Task EnabledRulesRequireAnOwnedEnabledSwitchableSocketButRetiredDraftsCanBeStopped()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = new RuleRepository(fixture.Factory("site-a"));
        foreach (var invalidTarget in fixture.InvalidTargets)
        {
            var before = await repository.GetAllAsync(default);
            await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateAsync(new()
            { Name = "Invalid target", EntityId = invalidTarget }, default));
            var edited = (await repository.GetByIdAsync(fixture.TargetId, default))!;
            edited.EntityId = invalidTarget;
            await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateAsync(edited, default));
            Assert.Equal(before.Count, (await repository.GetAllAsync(default)).Count);
            Assert.Equal(fixture.TargetSocket.ToString("D"), (await repository.GetByIdAsync(fixture.TargetId, default))!.EntityId);
        }
        var draft = await repository.CreateAsync(new() { Name = "Retired draft", EntityId = fixture.InvalidTargets[0], Enabled = false }, default);
        draft.Enabled = true;
        await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateAsync(draft, default));
        Assert.False((await repository.GetByIdAsync(draft.Id, default))!.Enabled);
        var current = (await repository.GetByIdAsync(fixture.TargetId, default))!;
        await using (var db = fixture.Factory("site-a").CreateDbContext())
        {
            (await db.IntegrationDeviceBindings.SingleAsync(device => device.Id == fixture.TargetSocket)).Enabled = false;
            await db.SaveChangesAsync();
        }
        current.Enabled = false;
        await repository.UpdateAsync(current, default);
        Assert.False((await repository.GetByIdAsync(current.Id, default))!.Enabled);
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

    private sealed class Fixture(DbContextOptions<DeyeSolarDbContext> options, SqlServerTestDatabase database) : IAsyncDisposable
    {
        public int TargetId { get; private set; }
        public Guid TargetSocket { get; } = Guid.NewGuid();
        public Guid ReplacementSocket { get; } = Guid.NewGuid();
        public Guid ContendedSocket { get; } = Guid.NewGuid();
        public string[] InvalidTargets { get; private set; } = [];
        public IDbContextFactory<DeyeSolarDbContext> Factory(string installation) => new Factory(options, installation);
        public async Task<string> NeighboursAsync()
        {
            await using var db = Factory("site-a").CreateDbContext();
            var rows = await db.TriggerRules.IgnoreQueryFilters().AsNoTracking().Where(rule => rule.Id != TargetId).OrderBy(rule => rule.Id).ToListAsync();
            return System.Text.Json.JsonSerializer.Serialize(rows);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var database = await SqlServerTestDatabase.CreateAsync("RuleStateTests");
            var fixture = new Fixture(database.Options, database);
            try
            {
                await using var db = fixture.Factory("site-a").CreateDbContext();
                db.Installations.AddRange(new Installation { Id = "site-a", CreatedAt = DateTimeOffset.UtcNow }, new Installation { Id = "site-b", CreatedAt = DateTimeOffset.UtcNow });
                var instance = new IntegrationInstanceEntity { Id = Guid.NewGuid(), ProviderId = "socket.fixture", State = "enabled" };
                var paused = new IntegrationInstanceEntity { Id = Guid.NewGuid(), ProviderId = "socket.fixture", State = "disabled" };
                db.IntegrationInstances.AddRange(instance, paused);
                Guid AddBinding(Guid connectionId, Guid? id = null, string kind = "socket", bool enabled = true, string metadata = "{\"capabilities\":{\"canSwitch\":true}}")
                {
                    var deviceId = id ?? Guid.NewGuid();
                    db.IntegrationDeviceBindings.Add(new() { Id = deviceId, InstanceId = connectionId, RemoteId = deviceId.ToString("D"),
                        Kind = kind, Enabled = enabled, MetadataJson = metadata });
                    return deviceId;
                }
                AddBinding(instance.Id, fixture.TargetSocket);
                AddBinding(instance.Id, fixture.ReplacementSocket);
                AddBinding(instance.Id, fixture.ContendedSocket);
                var otherSocket = AddBinding(instance.Id);
                fixture.InvalidTargets = ["raw-provider-id", Guid.Empty.ToString("D"), Guid.NewGuid().ToString("D"),
                    AddBinding(instance.Id, kind: "inverter").ToString("D"),
                    AddBinding(instance.Id, enabled: false).ToString("D"),
                    AddBinding(instance.Id, metadata: "{\"capabilities\":{\"canSwitch\":false}}").ToString("D"),
                    AddBinding(instance.Id, metadata: "invalid-json").ToString("D"),
                    AddBinding(paused.Id).ToString("D")];
                var target = new TriggerRule { Name = "Target", EntityId = fixture.TargetSocket.ToString("D"), SocTurnOnThreshold = 70 };
                db.TriggerRules.AddRange(target, new TriggerRule
                {
                    Name = "Independent neighbour",
                    EntityId = otherSocket.ToString("D"),
                    CurrentState = true,
                    CurrentStateChangedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
                    LastEvaluated = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc)
                });
                await db.SaveChangesAsync();
                fixture.TargetId = target.Id;
                await using var foreign = fixture.Factory("site-b").CreateDbContext();
                var foreignInstance = new IntegrationInstanceEntity { Id = Guid.NewGuid(), ProviderId = "socket.fixture", State = "enabled" };
                var foreignSocket = Guid.NewGuid();
                foreign.IntegrationInstances.Add(foreignInstance);
                foreign.IntegrationDeviceBindings.Add(new() { Id = foreignSocket, InstanceId = foreignInstance.Id, Kind = "socket", RemoteId = "foreign",
                    MetadataJson = "{\"capabilities\":{\"canSwitch\":true}}" });
                fixture.InvalidTargets = [.. fixture.InvalidTargets, foreignSocket.ToString("D")];
                foreign.TriggerRules.Add(new() { Name = "Foreign same socket", EntityId = fixture.TargetSocket.ToString("D"), Enabled = false, SocTurnOnThreshold = 11 });
                await foreign.SaveChangesAsync();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
}

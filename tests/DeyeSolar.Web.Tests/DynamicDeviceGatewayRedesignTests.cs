using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Redesign;
using Microsoft.EntityFrameworkCore;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Tests;

public sealed partial class DynamicDeviceGatewayTests
{
    [Theory]
    [InlineData("pause", false)]
    [InlineData("once", true)]
    [InlineData(null, true)]
    public async Task ManualChoicePersistsOneIntentAndPausesOnlyWhenRequested(string? policy, bool enabled)
    {
        await using var f = await Fixture.CreateAsync();
        var command = Guid.NewGuid();
        using var choice = ManualOverrideContext.Enter(policy, "actor", "app");
        var socket = await f.Sockets("a").GetForUserAsync(new(f.SocketA), "actor", _ => Task.CompletedTask, default);
        var request = new SetSocketPowerCommand(new(command), SwitchState.On);
        await socket.SetPowerAsync(request, default);
        await socket.SetPowerAsync(request, default);
        Assert.Equal(1, f.Executor.Sends);
        await using var db = f.Factory("a").CreateDbContext();
        var rule = await db.TriggerRules.SingleAsync();
        Assert.Equal(enabled, rule.Enabled);
        Assert.Equal(policy == "pause" ? "manual_override" : null, rule.PauseReason);
        Assert.Equal(policy == "pause" ? command : null, rule.PausedByCommandId);
        var receipt = Assert.Single(await db.IntegrationCommands.ToListAsync());
        Assert.Equal(policy, receipt.OnRuleConflict);
        Assert.Equal("actor", receipt.ActorUserId);
        Assert.Single(await db.ActivityEvents.Where(e => e.Kind == "command.manual").ToListAsync());
        Assert.Equal(policy == "pause" ? 1 : 0, await db.ActivityEvents.CountAsync(e => e.Kind == "rule.paused"));
        Assert.Single(await db.ActivityEvents.Where(e => e.Kind == "command.result").ToListAsync());
        await using var foreign = f.Factory("b").CreateDbContext();
        Assert.True((await foreign.TriggerRules.SingleAsync()).Enabled);
        Assert.Empty(await foreign.ActivityEvents.ToListAsync());
        if (policy == "pause")
        {
            rule.ConfigurationVersion = RuleConfigurationVersion.Read(rule);
            rule.Enabled = true;
            await new RuleRepository(f.Factory("a")).UpdateAsync(rule, default);
            Assert.Null(rule.PauseReason); Assert.Null(rule.PausedAt); Assert.Null(rule.PausedByUserId); Assert.Null(rule.PausedByCommandId);
            db.ChangeTracker.Clear();
            var resumed = await db.TriggerRules.SingleAsync();
            Assert.Null(resumed.PauseReason); Assert.Null(resumed.PausedAt); Assert.Null(resumed.PausedByUserId); Assert.Null(resumed.PausedByCommandId);
            Assert.Single(await db.ActivityEvents.Where(e => e.Kind == "rule.enabled").ToListAsync());
        }
    }

    [SqlServerFact]
    public async Task ReusingACommandIdWithAnotherOverrideChoiceRejectsWithoutAnotherEffect()
    {
        await using var f = await Fixture.CreateAsync();
        var socket = await f.Sockets("a").GetForUserAsync(new(f.SocketA), "actor", _ => Task.CompletedTask, default);
        var request = new SetSocketPowerCommand(new(Guid.NewGuid()), SwitchState.On);
        using (ManualOverrideContext.Enter("once", "actor", "web")) await socket.SetPowerAsync(request, default);
        using (ManualOverrideContext.Enter("pause", "actor", "web"))
            await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SetPowerAsync(request, default));
        Assert.Equal(1, f.Executor.Sends);
        await using var db = f.Factory("a").CreateDbContext();
        Assert.True((await db.TriggerRules.SingleAsync()).Enabled);
        Assert.Empty(await db.ActivityEvents.Where(e => e.Kind == "rule.paused").ToListAsync());
    }
}

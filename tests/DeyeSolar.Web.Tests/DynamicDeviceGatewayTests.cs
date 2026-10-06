using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Inverters.Contracts;
using SolarManagement.SmartSockets.Contracts;
using Xunit;

namespace DeyeSolar.Web.Tests;

public sealed partial class DynamicDeviceGatewayTests
{
    [SqlServerFact]
    public async Task AccountRevocationWhileQueuedRejectsBeforeIntentOrSecondRemoteEffect()
    {
        await using var f = await Fixture.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Executor.Send = async (_, parameters, _) => { started.SetResult(); await complete.Task; return Ack(parameters); };
        var revoked = false;
        Task Authorize(CancellationToken _) => revoked
            ? Task.FromException(new DeyeSolar.Web.Auth.InstallationAccessException("Revoked.")) : Task.CompletedTask;
        var socket = await f.Sockets("a").GetForUserAsync(new(f.SocketA), "actor", Authorize, default);
        var first = socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.On), default);
        await started.Task;
        var second = socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.Off), default);
        revoked = true;
        complete.SetResult();
        await first;
        await Assert.ThrowsAsync<DeyeSolar.Web.Auth.InstallationAccessException>(() => second);
        Assert.Equal(1, f.Executor.Sends);
        await using var db = f.Factory("a").CreateDbContext();
        Assert.Single(await db.IntegrationCommands.ToListAsync());
    }

    [SqlServerFact]
    public async Task AccountRevocationAfterIntentBeforeDispatchProducesTerminalRejectionWithoutEffect()
    {
        await using var f = await Fixture.CreateAsync();
        var checks = 0;
        Task Authorize(CancellationToken _) => ++checks > 1
            ? Task.FromException(new DeyeSolar.Web.Auth.InstallationAccessException("Revoked.")) : Task.CompletedTask;
        var socket = await f.Sockets("a").GetForUserAsync(new(f.SocketA), "actor", Authorize, default);
        Assert.Equal(SocketCommandStatus.Rejected, (await socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.On), default)).Status);
        Assert.Equal(0, f.Executor.Sends);
        await using var db = f.Factory("a").CreateDbContext();
        Assert.Equal("authorization_changed", (await db.IntegrationCommands.SingleAsync()).ErrorCode);
    }
    [SqlServerFact]
    public async Task TimeWindowOffUsesObservedPowerDuringInverterOutageWithoutCreatingAnOnCommand()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.SocketPower = true;
        await using (var db = f.Factory("a").CreateDbContext())
        {
            var rule = await db.TriggerRules.SingleAsync();
            rule.ActiveFrom = new TimeOnly(0, 0);
            rule.ActiveTo = new TimeOnly(0, 0);
            await db.SaveChangesAsync();
        }
        using var worker = Worker(f, _ => Task.FromException<InverterData>(new HttpRequestException()));
        await worker.PollAndEvaluateAsync(default);
        await using var check = f.Factory("a").CreateDbContext();
        var intent = await check.IntegrationCommands.SingleAsync();
        Assert.False(intent.DesiredState);
        Assert.Equal("acknowledged", intent.Status);
        Assert.False((await check.TriggerRules.SingleAsync()).CurrentState);
        Assert.Equal(1, f.Executor.Sends);
    }

    [SqlServerFact]
    public async Task UnknownPhysicalStateNeverAuthorizesOnAndIsExposedAsUnknown()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.SocketPower = null;
        using var worker = Worker(f, _ => Task.FromResult(new InverterData { BatterySoc = 100 }));
        await worker.PollAndEvaluateAsync(default);
        Assert.Equal(0, f.Executor.Sends);
        var device = Assert.Single(await f.Sockets("a").RefreshDevicesAsync(default));
        Assert.True(device.Online);
        Assert.False(device.StateKnown);
        await using var check = f.Factory("a").CreateDbContext();
        Assert.Empty(await check.IntegrationCommands.ToListAsync());
    }

    [SqlServerFact]
    public async Task AutonomousRecoveryReadsPendingReceiptsAndKeepsUncertainEffectsBlocked()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.Send = (_, p, _) => Task.FromResult(IntegrationJson.Element(new ProviderSocketCommandResult(p.GetProperty("commandId").GetString()!, "Pending", "operation-1")));
        var gateway = f.Sockets("a");
        var socket = await gateway.GetAsync(new(f.SocketA), default);
        await socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.On), default);
        await new SocketReceiptReconciler(f.Factory("a"), f.Sockets("a"), NullLogger.Instance).ReconcileAsync(default);
        Assert.Equal(1, f.Executor.Sends);
        Assert.Equal(1, f.Executor.Results);
        Assert.Empty(await gateway.ListUnresolvedAsync(new(f.SocketA), default));
        f.Executor.Send = (_, _, _) => throw new IOException();
        await socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.Off), default);
        await new SocketReceiptReconciler(f.Factory("a"), f.Sockets("a"), NullLogger.Instance).ReconcileAsync(default);
        Assert.Equal(SocketCommandStatus.Uncertain, Assert.Single(await gateway.ListUnresolvedAsync(new(f.SocketA), default)).Status);
        Assert.Equal(2, f.Executor.Sends);
    }

    [SqlServerFact]
    public async Task StaleObservationCannotOverwriteAcknowledgementThatArrivedDuringRead()
    {
        await using var f = await Fixture.CreateAsync();
        var repository = new RuleRepository(f.Factory("a"));
        var expected = Assert.Single(await repository.GetAllAsync(default));
        f.Executor.BeforeRead = async () =>
        {
            await using var db = f.Factory("a").CreateDbContext();
            var rule = await db.TriggerRules.SingleAsync();
            rule.CurrentState = true;
            rule.CurrentStateChangedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        };
        Assert.False(await new RuleObservationReconciler(f.Factory("a"), f.Sockets("a")).ReconcileAsync(expected, DateTime.UtcNow, default));
        Assert.True(Assert.Single(await repository.GetAllAsync(default)).CurrentState);
    }

    private static PollingWorker Worker(Fixture f, Func<CancellationToken, Task<InverterData>> refresh) => PollingWorkerFixture.Create(new Refresh(refresh),
        new FixedOptionsMonitor<InverterConnectionOptions>(new() { DeviceKey = f.InverterA.ToString("D") }), f.Sockets("a"),
        new RuleRepository(f.Factory("a")), new DeyeSolar.RuleEngine.RuleEvaluator(), f.Factory("a"),
        new FixedOptionsMonitor<PollingOptions>(new()), new AppSettingsService(f.Factory("a"), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Display:TimeZoneId"] = "UTC" }).Build()), NullLogger<PollingWorker>.Instance);
    [SqlServerFact]
    public async Task AcknowledgementAndReplayChangeOnlyTheOwnedSocketRules()
    {
        await using var f = await Fixture.CreateAsync();
        var gateway = f.Sockets("a");
        var socket = await gateway.GetAsync(new(f.SocketA), default);
        var command = new SetSocketPowerCommand(new(Guid.NewGuid()), SwitchState.On);
        Assert.Equal(SocketCommandStatus.Acknowledged, (await socket.SetPowerAsync(command, default)).Status);
        Assert.Equal(SocketCommandStatus.Acknowledged, (await socket.SetPowerAsync(command, default)).Status);
        Assert.Equal(1, f.Executor.Sends);
        await Assert.ThrowsAsync<ArgumentException>(() => socket.SetPowerAsync(new(new(Guid.NewGuid()), (SwitchState)99), default));
        await using var check = f.Factory("a").CreateDbContext();
        Assert.True((await check.TriggerRules.SingleAsync()).CurrentState);
        Assert.False((await check.TriggerRules.IgnoreQueryFilters().SingleAsync(r => r.InstallationId == "b")).CurrentState);
        Assert.Equal("acknowledged", (await check.IntegrationCommands.SingleAsync()).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SetPowerAsync(command with { DesiredState = SwitchState.Off }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Sockets("b").ReadResultAsync(new(f.SocketA), command.CommandId, default));
        Assert.Equal(1, f.Executor.Sends);
    }

    [SqlServerFact]
    public async Task UncertainIntentSurvivesRestartAndBlocksAnotherRemoteEffect()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.Send = (_, _, _) => throw new OperationCanceledException();
        var gateway = f.Sockets("a");
        var id = new SocketCommandId(Guid.NewGuid());
        var socket = await gateway.GetAsync(new(f.SocketA), default);
        Assert.Equal(SocketCommandStatus.Uncertain, (await socket.SetPowerAsync(new(id, SwitchState.On), default)).Status);
        var restarted = f.Sockets("a");
        var recovered = await restarted.ListUnresolvedAsync(new(f.SocketA), default);
        Assert.Equal(id, Assert.Single(recovered).CommandId);
        Assert.Equal(SocketCommandStatus.Uncertain, (await (await restarted.GetAsync(new(f.SocketA), default)).SetPowerAsync(new(id, SwitchState.On), default)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.Off), default));
        Assert.Equal(1, f.Executor.Sends);
        await using var check = f.Factory("a").CreateDbContext();
        Assert.False((await check.TriggerRules.SingleAsync()).CurrentState);
        Assert.Equal("cancelled_after_intent", (await check.IntegrationCommands.SingleAsync()).ErrorCode);
    }

    [SqlServerFact]
    public async Task AcknowledgementFromRetiredGenerationCannotApplyLocalState()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.Send = async (session, parameters, ct) =>
        {
            await using var db = f.Factory("a").CreateDbContext();
            var instance = await db.IntegrationInstances.SingleAsync(i => i.Id == session.InstanceId, ct);
            instance.Generation++;
            await db.SaveChangesAsync(ct);
            return Ack(parameters);
        };
        var socket = await f.Sockets("a").GetAsync(new(f.SocketA), default);
        var result = await socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.On), default);
        Assert.Equal(SocketCommandStatus.Uncertain, result.Status);
        await using var check = f.Factory("a").CreateDbContext();
        Assert.False((await check.TriggerRules.SingleAsync()).CurrentState);
        Assert.Equal("retired_generation", (await check.IntegrationCommands.SingleAsync()).ErrorCode);
    }

    [SqlServerFact]
    public async Task PendingResultUsesAuthoritativeReceiptAndNeverReplaysSet()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.Send = (_, p, _) => Task.FromResult(IntegrationJson.Element(new ProviderSocketCommandResult(p.GetProperty("commandId").GetString()!, "Pending", "operation-1")));
        var gateway = f.Sockets("a");
        var socket = await gateway.GetAsync(new(f.SocketA), default);
        var id = new SocketCommandId(Guid.NewGuid());
        Assert.Equal(SocketCommandStatus.Pending, (await socket.SetPowerAsync(new(id, SwitchState.On), default)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.Off), default));
        Assert.Equal(SocketCommandStatus.Acknowledged, (await gateway.ReadResultAsync(new(f.SocketA), id, default)).Status);
        Assert.Equal(1, f.Executor.Sends);
        Assert.Equal(1, f.Executor.Results);
        Assert.Empty(await gateway.ListUnresolvedAsync(new(f.SocketA), default));
        await using var check = f.Factory("a").CreateDbContext();
        Assert.True((await check.TriggerRules.SingleAsync()).CurrentState);
        Assert.False((await check.TriggerRules.IgnoreQueryFilters().SingleAsync(r => r.InstallationId == "b")).CurrentState);
    }

    [SqlServerFact]
    public async Task ObservationExpiresWhileAuthorizationWaitsCannotReleaseAnUncertainCommand()
    {
        await using var f = await Fixture.CreateAsync();
        var clock = new Clock(DateTimeOffset.UtcNow);
        f.Executor.Send = (_, _, _) => throw new IOException();
        var gateway = f.Sockets("a", clock);
        var socket = await gateway.GetAsync(new(f.SocketA), default);
        var id = new SocketCommandId(Guid.NewGuid());
        Assert.Equal(SocketCommandStatus.Uncertain, (await socket.SetPowerAsync(new(id, SwitchState.On), default)).Status);
        f.Executor.Age = TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1);
        var authorizing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueAuthorization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        async Task Authorize(CancellationToken ct)
        {
            if (++checks != 2) return;
            authorizing.SetResult();
            await continueAuthorization.Task.WaitAsync(ct);
        }
        var release = gateway.ReleaseForUserAsync(new(f.SocketA), id, "actor", Authorize, default);
        await authorizing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Now = clock.Now.AddMinutes(2);
        continueAuthorization.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => release);
        Assert.Single(await gateway.ListUnresolvedAsync(new(f.SocketA), default));
        Assert.Equal(1, f.Executor.Sends);
        await using var db = f.Factory("a").CreateDbContext();
        Assert.Equal("uncertain", (await db.IntegrationCommands.SingleAsync()).Status);
    }

    [SqlServerFact]
    public async Task AutomaticDecisionExpiredAfterIntentIsRejectedWithoutProviderDispatch()
    {
        await using var f = await Fixture.CreateAsync();
        var gateway = f.Sockets("a");
        var decisionCurrent = true;
        var checks = 0;
        Task Authorize(CancellationToken ct)
        {
            if (++checks == 2) decisionCurrent = false;
            return Task.CompletedTask;
        }
        var socket = await gateway.GetForUserAsync(new(f.SocketA), "actor", Authorize, default);
        var rule = Assert.Single(await new RuleRepository(f.Factory("a")).GetAllAsync(default));
        using var decision = IntegrationAutomationSourceGuard.Enter(null, rule,
            ConfirmedInverterReading.Create(new InverterData { BatterySoc = 95, Timestamp = DateTimeOffset.UtcNow }), true, () => decisionCurrent);
        var result = await socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.On), default);
        Assert.Equal(SocketCommandStatus.Rejected, result.Status);
        Assert.Equal(0, f.Executor.Sends);
        Assert.Empty(await gateway.ListUnresolvedAsync(new(f.SocketA), default));
        await using var db = f.Factory("a").CreateDbContext();
        Assert.Equal("condition_changed", (await db.IntegrationCommands.SingleAsync()).ErrorCode);
        Assert.False((await db.TriggerRules.SingleAsync()).CurrentState);
    }

    [SqlServerFact]
    public async Task AccessRevokedDuringObservationCannotReleaseAnUncertainCommand()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.Send = (_, _, _) => throw new IOException();
        var gateway = f.Sockets("a");
        var socket = await gateway.GetAsync(new(f.SocketA), default);
        var id = new SocketCommandId(Guid.NewGuid());
        Assert.Equal(SocketCommandStatus.Uncertain, (await socket.SetPowerAsync(new(id, SwitchState.On), default)).Status);
        var revoked = false;
        f.Executor.BeforeRead = () => { revoked = true; return Task.CompletedTask; };
        Task Authorize(CancellationToken ct) => revoked
            ? Task.FromException(new DeyeSolar.Web.Auth.InstallationAccessException("Access revoked.")) : Task.CompletedTask;
        await Assert.ThrowsAsync<DeyeSolar.Web.Auth.InstallationAccessException>(() => gateway.ReleaseForUserAsync(
            new(f.SocketA), id, "actor", Authorize, default));
        Assert.Single(await gateway.ListUnresolvedAsync(new(f.SocketA), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.Off), default));
        Assert.Equal(1, f.Executor.Sends);
    }

    [SqlServerFact]
    public async Task StalePhysicalObservationCannotReleaseAnUncertainCommand()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.Send = (_, _, _) => throw new IOException();
        var gateway = f.Sockets("a");
        var socket = await gateway.GetAsync(new(f.SocketA), default);
        var id = new SocketCommandId(Guid.NewGuid());
        Assert.Equal(SocketCommandStatus.Uncertain, (await socket.SetPowerAsync(new(id, SwitchState.On), default)).Status);
        f.Executor.Age = TimeSpan.FromMinutes(11);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ReleaseAsync(new(f.SocketA), id, default));
        Assert.Single(await gateway.ListUnresolvedAsync(new(f.SocketA), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.Off), default));
        Assert.Equal(1, f.Executor.Sends);
    }

    [SqlServerFact]
    public async Task ExplicitReleaseKeepsUnknownResultAndAllowsNewIntentAfterObservation()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.Send = (_, _, _) => throw new IOException();
        var gateway = f.Sockets("a");
        var socket = await gateway.GetAsync(new(f.SocketA), default);
        var id = new SocketCommandId(Guid.NewGuid());
        await socket.SetPowerAsync(new(id, SwitchState.On), default);
        Assert.Equal(SocketCommandStatus.UncertainClosed, (await gateway.ReleaseAsync(new(f.SocketA), id, default)).Status);
        Assert.Empty(await gateway.ListUnresolvedAsync(new(f.SocketA), default));
        Assert.Equal(SocketCommandStatus.UncertainClosed, (await gateway.ReadResultAsync(new(f.SocketA), id, default)).Status);
        f.Executor.Send = (_, p, _) => Task.FromResult(Ack(p));
        Assert.Equal(SocketCommandStatus.Acknowledged, (await socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.Off), default)).Status);
        Assert.Equal(2, f.Executor.Sends);
    }

    [SqlServerFact]
    public async Task PendingWithoutAResultIdentityRemainsUncertainAndCannotBeReplayed()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.Send = (_, p, _) => Task.FromResult(IntegrationJson.Element(new ProviderSocketCommandResult(p.GetProperty("commandId").GetString()!, "Pending")));
        var gateway = f.Sockets("a");
        var socket = await gateway.GetAsync(new(f.SocketA), default);
        var id = new SocketCommandId(Guid.NewGuid());
        Assert.Equal(SocketCommandStatus.Uncertain, (await socket.SetPowerAsync(new(id, SwitchState.On), default)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.Off), default));
        Assert.Equal(SocketCommandStatus.Uncertain, (await gateway.ReadResultAsync(new(f.SocketA), id, default)).Status);
        Assert.Equal(1, f.Executor.Sends);
        Assert.Equal(0, f.Executor.Results);
        await using var check = f.Factory("a").CreateDbContext();
        Assert.False((await check.TriggerRules.SingleAsync()).CurrentState);
        Assert.False((await check.TriggerRules.IgnoreQueryFilters().SingleAsync(r => r.InstallationId == "b")).CurrentState);
        Assert.Equal("uncertain", (await check.IntegrationCommands.SingleAsync()).Status);
    }

    [SqlServerFact]
    public async Task PendingCannotBeReleasedWhileTheProviderMayStillExecuteIt()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.Send = (_, p, _) => Task.FromResult(IntegrationJson.Element(new ProviderSocketCommandResult(p.GetProperty("commandId").GetString()!, "Pending", "operation-1")));
        f.Executor.Result = p => IntegrationJson.Element(new ProviderSocketCommandResult(p.GetProperty("commandId").GetString()!, "Pending", "operation-1"));
        var gateway = f.Sockets("a");
        var socket = await gateway.GetAsync(new(f.SocketA), default);
        var id = new SocketCommandId(Guid.NewGuid());
        await socket.SetPowerAsync(new(id, SwitchState.On), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ReleaseAsync(new(f.SocketA), id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.Off), default));
        await using (var check = f.Factory("a").CreateDbContext())
        {
            Assert.Equal("pending", (await check.IntegrationCommands.SingleAsync()).Status);
            Assert.False((await check.TriggerRules.SingleAsync()).CurrentState);
            Assert.False((await check.TriggerRules.IgnoreQueryFilters().SingleAsync(r => r.InstallationId == "b")).CurrentState);
        }
        f.Executor.Result = Ack;
        Assert.Equal(SocketCommandStatus.Acknowledged, (await gateway.ReadResultAsync(new(f.SocketA), id, default)).Status);
        Assert.Equal(1, f.Executor.Sends);
    }

    [SqlServerFact]
    public async Task RetiredPendingCommandBecomesUncertainWithoutQueryingAnotherGeneration()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.Send = (_, p, _) => Task.FromResult(IntegrationJson.Element(new ProviderSocketCommandResult(p.GetProperty("commandId").GetString()!, "Pending", "operation-1")));
        var gateway = f.Sockets("a");
        var socket = await gateway.GetAsync(new(f.SocketA), default);
        var id = new SocketCommandId(Guid.NewGuid());
        await socket.SetPowerAsync(new(id, SwitchState.On), default);
        await using (var db = f.Factory("a").CreateDbContext())
        {
            var instance = await db.IntegrationInstances.SingleAsync();
            instance.Generation++;
            await db.SaveChangesAsync();
        }
        Assert.Equal(SocketCommandStatus.Uncertain, (await gateway.ReadResultAsync(new(f.SocketA), id, default)).Status);
        Assert.Equal(0, f.Executor.Results);
        var replacement = await gateway.GetAsync(new(f.SocketA), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.Off), default));
        await using var check = f.Factory("a").CreateDbContext();
        Assert.Equal("retired_generation", (await check.IntegrationCommands.SingleAsync()).ErrorCode);
        Assert.False((await check.TriggerRules.SingleAsync()).CurrentState);
        Assert.False((await check.TriggerRules.IgnoreQueryFilters().SingleAsync(r => r.InstallationId == "b")).CurrentState);
        Assert.Equal(1, f.Executor.Sends);
    }

    [SqlServerFact]
    public async Task MissingMeasurementsRemainMissingWhileARealZeroRetainsProvenance()
    {
        await using var f = await Fixture.CreateAsync();
        var gateway = f.Inverters("a");
        f.Executor.Missing = true;
        var missing = await gateway.ReadDeviceAsync(new(f.InverterA), default);
        Assert.False(missing.BatterySocValid);
        Assert.Null(missing.SolarObservedAt);
        Assert.Null(missing.GridObservedAt);
        f.Executor.Missing = false;
        var zero = await gateway.ReadDeviceAsync(new(f.InverterA), default);
        Assert.True(zero.BatterySocValid);
        Assert.Equal(0, zero.SolarProduction);
        Assert.Equal(f.InverterA.ToString("D"), zero.SolarDeviceSn);
        Assert.NotNull(zero.SolarObservedAt);
        await new ExportReadingStore(f.Factory("a"), TimeProvider.System).SavePollingAsync(zero, default);
        await using var check = f.Factory("a").CreateDbContext();
        Assert.Equal(f.InverterA, (await check.Readings.SingleAsync()).InverterId);
        Assert.Equal(0, (await check.ExportReadings.SingleAsync()).GridPowerWatts);
        Assert.Empty(await check.Readings.IgnoreQueryFilters().Where(r => r.InstallationId == "b").ToListAsync());
    }

    [SqlServerFact]
    public async Task RetiredTelemetryRollsBackWithoutChangingEitherHistoryOrNeighbour()
    {
        await using var f = await Fixture.CreateAsync();
        var data = await f.Inverters("a").ReadDeviceAsync(new(f.InverterA), default);
        await using (var db = f.Factory("a").CreateDbContext())
        {
            var instance = await db.IntegrationInstances.SingleAsync();
            instance.Generation++;
            await db.SaveChangesAsync();
        }
        var store = new ExportReadingStore(f.Factory("a"), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SavePollingAsync(data, default));
        await using var check = f.Factory("a").CreateDbContext();
        Assert.Empty(await check.Readings.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await check.ExportReadings.IgnoreQueryFilters().ToListAsync());
    }

    [SqlServerFact]
    public async Task NullGoodStaleAndFutureMeasurementsCannotDriveBatteryRules()
    {
        await using var f = await Fixture.CreateAsync();
        var source = f.Inverters("a");
        f.Executor.NullGood = true;
        Assert.False((await source.ReadDeviceAsync(new(f.InverterA), default)).BatterySocValid);
        f.Executor.NullGood = false;
        f.Executor.Age = TimeSpan.FromMinutes(11);
        var stale = await (await source.GetAsync(new(f.InverterA), default)).ReadAsync(default);
        Assert.Equal(MeasurementQuality.Stale, stale.BatterySoc.Quality);
        Assert.NotNull(stale.BatterySoc.Value);
        Assert.False((await source.ReadDeviceAsync(new(f.InverterA), default)).BatterySocValid);
        f.Executor.Age = TimeSpan.FromMinutes(-1);
        Assert.Equal(MeasurementQuality.Invalid, (await (await source.GetAsync(new(f.InverterA), default)).ReadAsync(default)).BatterySoc.Quality);
        Assert.False((await source.ReadDeviceAsync(new(f.InverterA), default)).BatterySocValid);
        f.Executor.Age = TimeSpan.FromSeconds(2);
        var zero = await (await source.GetAsync(new(f.InverterA), default)).ReadAsync(default);
        Assert.Equal(MeasurementQuality.Good, zero.BatterySoc.Quality);
        Assert.Equal(0, zero.BatterySoc.Value!.Value.Value);
    }

    [Fact]
    public void DeviceContractsHaveNoVendorOrHostReferences()
    {
        foreach (var assembly in new[] { typeof(IInverter).Assembly, typeof(ISmartSocket).Assembly })
            Assert.All(assembly.GetReferencedAssemblies(), reference => Assert.StartsWith("System.", reference.Name));
        foreach (var assembly in new[] { typeof(TriggerRule).Assembly, typeof(DeyeSolar.RuleEngine.RuleEvaluator).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("SolarManagement.Providers.", StringComparison.Ordinal));
    }

    [SqlServerFact]
    public async Task RuleGroupsUseTheirOwnInverterWithoutAveragingAcrossSourcesOrTenants()
    {
        await using var f = await Fixture.CreateAsync();
        var secondary = Guid.NewGuid();
        var target = Guid.NewGuid();
        await using (var db = f.Factory("a").CreateDbContext())
        {
            var instance = await db.IntegrationInstances.SingleAsync();
            db.IntegrationDeviceBindings.Add(new()
            {
                Id = secondary,
                InstanceId = instance.Id,
                RemoteId = "OtherSn",
                Kind = "inverter",
                MetadataJson = "{\"capabilities\":{\"hasBattery\":true,\"hasSolarPower\":true}}"
            });
            db.IntegrationDeviceBindings.Add(new()
            {
                Id = target,
                InstanceId = instance.Id,
                RemoteId = "OtherSocket",
                Channel = "0",
                Kind = "socket",
                MetadataJson = "{\"capabilities\":{\"canSwitch\":true}}"
            });
            var primaryRule = await db.TriggerRules.SingleAsync(); primaryRule.SocTurnOnThreshold = 80;
            db.TriggerRules.Add(new() { Name = "Secondary", EntityId = target.ToString("D"), SourceInverterId = secondary, SocTurnOnThreshold = 80 });
            await db.SaveChangesAsync();
        }
        f.Executor.SocByRemote["CaseSensitiveSn"] = 20;
        f.Executor.SocByRemote["OtherSn"] = 90;
        var source = f.Inverters("a");
        var store = new ExportReadingStore(f.Factory("a"), TimeProvider.System);
        var socket = f.Sockets("a");
        using var worker = PollingWorkerFixture.Create(new Refresh(async ct =>
        {
            var data = await source.ReadDeviceAsync(new(f.InverterA), ct);
            await store.SavePollingAsync(data, ct); return data;
        }), new FixedOptionsMonitor<InverterConnectionOptions>(new() { DeviceKey = f.InverterA.ToString("D") }), socket,
            new RuleRepository(f.Factory("a")), new DeyeSolar.RuleEngine.RuleEvaluator(), f.Factory("a"),
            new FixedOptionsMonitor<PollingOptions>(new()), new AppSettingsService(f.Factory("a"), new ConfigurationBuilder().Build()),
            NullLogger<PollingWorker>.Instance, source, store);
        await (Task)typeof(PollingWorker).GetMethod("PollAndEvaluateAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(worker, [CancellationToken.None])!;
        await using var check = f.Factory("a").CreateDbContext();
        Assert.Equal(target, (await check.IntegrationCommands.SingleAsync()).DeviceId);
        Assert.Equal(1, f.Executor.Sends);
        Assert.False((await check.TriggerRules.SingleAsync(r => r.EntityId == f.SocketA.ToString("D"))).CurrentState);
        Assert.True((await check.TriggerRules.SingleAsync(r => r.SourceInverterId == secondary)).CurrentState);
        Assert.False((await check.TriggerRules.IgnoreQueryFilters().SingleAsync(r => r.InstallationId == "b")).CurrentState);
        Assert.Equal(new[] { f.InverterA, secondary }.Order(), (await check.Readings.Select(r => r.InverterId!.Value).ToListAsync()).Order());
        Assert.Empty(await check.Readings.IgnoreQueryFilters().Where(r => r.InstallationId == "b").ToListAsync());
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Refresh(Func<CancellationToken, Task<InverterData>> read) : IInverterRefreshService
    {
        public Task<InverterData> RefreshAsync(CancellationToken ct) => read(ct);
    }

    private static JsonElement Ack(JsonElement p) => IntegrationJson.Element(new ProviderSocketCommandResult(p.GetProperty("commandId").GetString()!, "Acknowledged"));
    private sealed class Executor : IIntegrationRuntimeExecutor
    {
        public int Sends;
        public int Results;
        public bool Missing;
        public bool NullGood;
        public bool? SocketPower = false;
        public Func<Task>? BeforeRead;
        public TimeSpan Age = TimeSpan.FromSeconds(2);
        public Dictionary<string, int> SocByRemote = [];
        public Func<JsonElement, JsonElement> Result = Ack;
        public Func<IntegrationSession, JsonElement, CancellationToken, Task<JsonElement>> Send = (_, p, _) => Task.FromResult(Ack(p));
        public Task StopAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<JsonElement> InvokeAsync(IntegrationSession session, string method, JsonElement p, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (method == "socket.set") { Interlocked.Increment(ref Sends); return Send(session, p, ct); }
            if (method == "socket.result") { Interlocked.Increment(ref Results); return Task.FromResult(Result(p)); }
            var remote = p.GetProperty("remoteId").GetString()!;
            var observed = DateTimeOffset.UtcNow - Age;
            if (method == "socket.read") return ReadSocketAsync(remote, observed);
            var value = new ProviderMeasurement(Missing ? null : 0, Missing ? null : observed,
                Missing ? ProviderMeasurementQuality.Missing : ProviderMeasurementQuality.Good);
            return Task.FromResult(IntegrationJson.Element(new ProviderInverterTelemetry(remote, DateTimeOffset.UtcNow, "Unknown",
                Missing ? value : value with { Value = NullGood ? null : SocByRemote.GetValueOrDefault(remote) }, value, value, value, value, value, value, value)));
        }
        private async Task<JsonElement> ReadSocketAsync(string remote, DateTimeOffset observed)
        {
            if (BeforeRead is not null) await BeforeRead();
            return IntegrationJson.Element(new ProviderSocketTelemetry(remote, "0", SocketPower, true, 0, observed, DateTimeOffset.UtcNow));
        }
    }
    private sealed class Fixture(SqlServerTestDatabase database) : IAsyncDisposable
    {
        public Guid SocketA = Guid.NewGuid();
        public Guid InverterA = Guid.NewGuid();
        public Executor Executor = new();
        private readonly IntegrationSecretStore _secrets = new(new EphemeralDataProtectionProvider());
        public Factory Factory(string installation) => new(database.Options, installation);
        public DynamicSocketGateway Sockets(string installation, TimeProvider? clock = null) => new(new IntegrationRegistry(Factory(installation), _secrets), Executor, Factory(installation), clock ?? TimeProvider.System);
        public DynamicInverterGateway Inverters(string installation)
        {
            var registry = new IntegrationRegistry(Factory(installation), _secrets);
            return new(registry, Executor, new(registry), TimeProvider.System);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var database = await SqlServerTestDatabase.CreateAsync("DynamicDevices", SqlTestSchema.Model, seed: async db =>
            {
                db.Installations.AddRange(new Installation { Id = "a" }, new Installation { Id = "b" });
                await db.SaveChangesAsync();
            });
            var f = new Fixture(database);
            try
            {
                foreach (var tenant in new[] { "a", "b" })
                {
                    await using var db = f.Factory(tenant).CreateDbContext();
                    var instance = new IntegrationInstanceEntity { Id = Guid.NewGuid(), ProviderId = "unknown.vendor", PackageVersion = "1", PackageDigest = "digest", State = "enabled" };
                    db.Add(instance);
                    db.Add(new IntegrationConfigurationEntity { InstanceId = instance.Id, Revision = 1 });
                    var socket = tenant == "a" ? f.SocketA : Guid.NewGuid();
                    db.Add(new IntegrationDeviceBindingEntity
                    {
                        Id = socket,
                        InstanceId = instance.Id,
                        RemoteId = "SameOpaqueId",
                        Channel = "0",
                        Kind = "socket",
                        MetadataJson = "{\"capabilities\":{\"canSwitch\":true,\"canMeasurePower\":true}}"
                    });
                    db.Add(new IntegrationDeviceBindingEntity
                    {
                        Id = tenant == "a" ? f.InverterA : Guid.NewGuid(),
                        InstanceId = instance.Id,
                        RemoteId = "CaseSensitiveSn",
                        Kind = "inverter",
                        IsDefault = true,
                        MetadataJson = "{\"capabilities\":{\"hasBattery\":true,\"hasSolarPower\":true,\"hasSignedGridPower\":true}}"
                    });
                    db.TriggerRules.Add(new TriggerRule { EntityId = socket.ToString("D"), Name = "Independent rule" });
                    await db.SaveChangesAsync();
                }
                return f;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options, string installation) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options, installation);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}

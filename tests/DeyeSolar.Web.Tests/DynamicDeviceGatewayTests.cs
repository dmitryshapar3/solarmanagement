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
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Inverters.Contracts;
using SolarManagement.SmartSockets.Contracts;
using Xunit;

namespace DeyeSolar.Web.Tests;

public sealed class DynamicDeviceGatewayTests
{
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
        using var worker = new PollingWorker(new Refresh(async ct =>
        {
            var data = await source.ReadDeviceAsync(new(f.InverterA), ct);
            await store.SavePollingAsync(data, ct); return data;
        }), new Monitor<InverterConnectionOptions>(new() { DeviceKey = f.InverterA.ToString("D") }), socket,
            new RuleRepository(f.Factory("a")), new DeyeSolar.RuleEngine.RuleEvaluator(), f.Factory("a"),
            new Monitor<PollingOptions>(new()), new AppSettingsService(f.Factory("a"), new ConfigurationBuilder().Build()),
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
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
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
            if (method == "socket.read") return Task.FromResult(IntegrationJson.Element(new ProviderSocketTelemetry(remote, "0", false, true, 0, observed, DateTimeOffset.UtcNow)));
            var value = new ProviderMeasurement(Missing ? null : 0, Missing ? null : observed,
                Missing ? ProviderMeasurementQuality.Missing : ProviderMeasurementQuality.Good);
            return Task.FromResult(IntegrationJson.Element(new ProviderInverterTelemetry(remote, DateTimeOffset.UtcNow, "Unknown",
                Missing ? value : value with { Value = NullGood ? null : SocByRemote.GetValueOrDefault(remote) }, value, value, value, value, value, value, value)));
        }
    }
    private sealed class Fixture(DbContextOptions<DeyeSolarDbContext> options) : IAsyncDisposable
    {
        public Guid SocketA = Guid.NewGuid();
        public Guid InverterA = Guid.NewGuid();
        public Executor Executor = new();
        private readonly IntegrationSecretStore _secrets = new(new EphemeralDataProtectionProvider());
        public Factory Factory(string installation) => new(options, installation);
        public DynamicSocketGateway Sockets(string installation) => new(new IntegrationRegistry(Factory(installation), _secrets), Executor, Factory(installation), TimeProvider.System);
        public DynamicInverterGateway Inverters(string installation)
        {
            var registry = new IntegrationRegistry(Factory(installation), _secrets);
            return new(registry, Executor, new(registry), TimeProvider.System);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
            { InitialCatalog = "DynamicDevices_" + Guid.NewGuid().ToString("N") };
            var f = new Fixture(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options);
            await using (var db = f.Factory("a").CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Installations.AddRange(new Installation { Id = "a" }, new Installation { Id = "b" });
                await db.SaveChangesAsync();
            }
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
        public async ValueTask DisposeAsync() { await using var db = Factory("a").CreateDbContext(); await db.Database.EnsureDeletedAsync(); }
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options, string installation) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options, installation);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}

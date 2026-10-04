using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SolarManagement.Integrations.Contracts;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Tests;

public sealed class DeviceGatewayConcurrencyTests
{
    [SqlServerFact]
    public async Task DelayedPendingResponseCannotOverwriteAcknowledgedOrExplicitlyClosedDurableWinner()
    {
        foreach (var closeRetired in new[] { false, true })
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
            { InitialCatalog = "GatewayReceiptRace_" + Guid.NewGuid().ToString("N") };
            var options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options;
            var factory = new Factory(options, "a");
            var instanceId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var commandId = new SocketCommandId(Guid.NewGuid());
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var results = 0;
            var executor = new Executor(async (_, method, p, ct) =>
            {
                if (method == "socket.read") return IntegrationJson.Element(new ProviderSocketTelemetry("same-remote", "0", false, true, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                var id = p.GetProperty("commandId").GetString()!;
                if (method == "socket.result" && Interlocked.Increment(ref results) == 1)
                {
                    started.SetResult();
                    await release.Task.WaitAsync(ct);
                    return IntegrationJson.Element(new ProviderSocketCommandResult(id, "Pending", "remote-operation"));
                }
                return IntegrationJson.Element(new ProviderSocketCommandResult(id, method == "socket.set" ? "Pending" : "Acknowledged", "remote-operation"));
            });
            var registry = new IntegrationRegistry(factory, new IntegrationSecretStore(new EphemeralDataProtectionProvider()));
            var gateway = new DynamicSocketGateway(registry, executor, factory, TimeProvider.System);
            try
            {
                await using (var db = factory.CreateDbContext())
                {
                    await db.Database.EnsureCreatedAsync();
                    db.Installations.AddRange(new Installation { Id = "a" }, new Installation { Id = "b" });
                    db.IntegrationInstances.Add(new() { Id = instanceId, ProviderId = "test.provider", PackageVersion = "1.0.0", PackageDigest = "fixture", State = "enabled" });
                    db.IntegrationConfigurations.Add(new() { InstanceId = instanceId, Revision = 1 });
                    db.IntegrationDeviceBindings.Add(new() { Id = deviceId, InstanceId = instanceId, RemoteId = "same-remote", Channel = "0", Kind = "socket", MetadataJson = "{\"capabilities\":{\"canSwitch\":true}}" });
                    db.TriggerRules.Add(new() { EntityId = deviceId.ToString("D"), Name = "Owned" });
                    await db.SaveChangesAsync();
                }
                await using (var db = new Factory(options, "b").CreateDbContext())
                {
                    db.TriggerRules.Add(new() { EntityId = deviceId.ToString("D"), Name = "Neighbor" });
                    await db.SaveChangesAsync();
                }
                var socket = await gateway.GetAsync(new(deviceId), default);
                await socket.SetPowerAsync(new(commandId, SwitchState.On), default);
                var delayed = gateway.ReadResultAsync(new(deviceId), commandId, default);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                SocketCommandStatus expected;
                if (closeRetired)
                {
                    await using (var db = factory.CreateDbContext())
                    {
                        var instance = await db.IntegrationInstances.SingleAsync();
                        instance.Generation++;
                        await db.SaveChangesAsync();
                    }
                    expected = SocketCommandStatus.UncertainClosed;
                    Assert.Equal(expected, (await gateway.ReleaseAsync(new SocketId(deviceId), commandId, default)).Status);
                }
                else
                {
                    expected = SocketCommandStatus.Acknowledged;
                    Assert.Equal(expected, (await gateway.ReadResultAsync(new(deviceId), commandId, default)).Status);
                }
                release.SetResult();
                Assert.Equal(expected, (await delayed).Status);
                await using var check = factory.CreateDbContext();
                Assert.Equal(closeRetired ? "uncertain_closed" : "acknowledged", (await check.IntegrationCommands.SingleAsync()).Status);
                Assert.Equal(!closeRetired, (await check.TriggerRules.SingleAsync()).CurrentState);
                Assert.False((await check.TriggerRules.IgnoreQueryFilters().SingleAsync(r => r.InstallationId == "b")).CurrentState);
                Assert.Empty(await check.IntegrationCommands.IgnoreQueryFilters().Where(c => c.InstallationId == "b").ToArrayAsync());
            }
            finally
            {
                release.TrySetResult();
                await using var db = factory.CreateDbContext();
                await db.Database.EnsureDeletedAsync();
            }
        }
    }

    [Fact]
    public async Task InvalidationDuringBindingReadCannotRepublishRetiredInventory()
    {
        var old = Binding("Retired");
        var current = Binding("Current");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var registry = new Registry([old, current], async ct =>
        {
            if (Interlocked.Increment(ref calls) != 1) return [current];
            started.SetResult();
            await release.Task.WaitAsync(ct);
            return [old];
        });
        var gateway = new DynamicSocketGateway(registry, ReadExecutor(), new UnusedFactory(), TimeProvider.System);
        var delayed = gateway.RefreshDevicesAsync(default);
        await started.Task;
        gateway.Invalidate();
        Assert.Equal(current.Id.ToString("D"), Assert.Single(await gateway.RefreshDevicesAsync(default)).Id);
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => delayed);
        Assert.Equal(current.Id.ToString("D"), Assert.Single(await gateway.GetCachedDevicesAsync(default)).Id);
        Assert.Equal(current.Id, Assert.Single((await gateway.ReadInventoryAsync(false, default)).Devices).Id.Value);
    }

    [Fact]
    public async Task OlderConcurrentRefreshCannotReplaceNewerTypedAndLegacyCache()
    {
        var binding = Binding("Socket");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var executor = new Executor(async (_, _, p, ct) =>
        {
            var old = Interlocked.Increment(ref reads) == 1;
            if (old) { started.SetResult(); await release.Task.WaitAsync(ct); }
            return IntegrationJson.Element(new ProviderSocketTelemetry(p.GetProperty("remoteId").GetString()!, "0", !old, true, old ? 100 : 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        });
        var gateway = new DynamicSocketGateway(new Registry([binding], _ => Task.FromResult<IReadOnlyList<IntegrationDeviceBindingEntity>>([binding])), executor, new UnusedFactory(), TimeProvider.System);
        var delayed = gateway.RefreshDevicesAsync(default);
        await started.Task;
        Assert.Equal(200, Assert.Single(await gateway.RefreshDevicesAsync(default)).CurrentPowerW);
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => delayed);
        Assert.Equal(200, Assert.Single(await gateway.GetCachedDevicesAsync(default)).CurrentPowerW);
        Assert.Equal(binding.Id, Assert.Single((await gateway.ReadInventoryAsync(false, default)).Devices).Id.Value);
        Assert.Equal(2, reads);
    }

    private static IntegrationDeviceBindingEntity Binding(string name) => new() { Id = Guid.NewGuid(), InstanceId = Guid.NewGuid(), RemoteId = name, Name = name, Channel = "0", Kind = "socket", MetadataJson = "{\"capabilities\":{\"canSwitch\":true,\"canMeasurePower\":true}}" };
    private static Executor ReadExecutor() => new((_, _, p, _) => Task.FromResult(IntegrationJson.Element(new ProviderSocketTelemetry(p.GetProperty("remoteId").GetString()!, "0", false, true, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))));
    private sealed class Executor(Func<IntegrationSession, string, JsonElement, CancellationToken, Task<JsonElement>> invoke) : IIntegrationRuntimeExecutor
    {
        public Task<JsonElement> InvokeAsync(IntegrationSession session, string method, JsonElement parameters, CancellationToken ct) => invoke(session, method, parameters, ct);
        public Task StopAsync(Guid instanceId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Registry(IntegrationDeviceBindingEntity[] bindings, Func<CancellationToken, Task<IReadOnlyList<IntegrationDeviceBindingEntity>>> list) : IIntegrationRegistry
    {
        public Task<IReadOnlyList<IntegrationDeviceBindingEntity>> ListBindingsAsync(CancellationToken ct) => list(ct);
        public Task<IntegrationDeviceBindingEntity?> FindBindingAsync(Guid id, CancellationToken ct) => Task.FromResult(bindings.SingleOrDefault(b => b.Id == id));
        public Task<IntegrationDeviceBindingEntity?> GetPrimaryInverterAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IntegrationRegistrySnapshot?> GetSnapshotAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IntegrationSession> GetRuntimeSessionAsync(Guid id, CancellationToken ct) => Task.FromResult(new IntegrationSession("a", id, new("test.provider", "1.0.0", "fixture"), 1, 1, new(IntegrationJson.Element(new { }), new Dictionary<string, string>())));
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options, string installation) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options, installation);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class UnusedFactory : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => throw new InvalidOperationException("Inventory must not open the command journal.");
    }
}

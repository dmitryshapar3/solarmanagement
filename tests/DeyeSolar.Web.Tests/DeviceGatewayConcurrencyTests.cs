using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;
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
            await using var database = await SqlServerTestDatabase.CreateAsync("GatewayReceiptRace", SqlTestSchema.Model);
            var options = database.Options;
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
        var snapshot = new DeviceStatusSnapshot();
        async Task<IReadOnlyList<DevicePowerInfo>> RefreshAndPublishAsync()
        {
            var epoch = snapshot.Epoch;
            var devices = await gateway.RefreshDevicesAsync(default);
            Assert.True(snapshot.TryUpdate(devices, epoch));
            return devices;
        }
        var delayed = RefreshAndPublishAsync();
        await started.Task;
        Assert.Equal(200, Assert.Single(await RefreshAndPublishAsync()).CurrentPowerW);
        release.SetResult();
        Assert.Equal(200, Assert.Single(await delayed).CurrentPowerW);
        Assert.Equal(200, Assert.Single(snapshot.Current!).CurrentPowerW);
        Assert.Equal(200, Assert.Single(await gateway.GetCachedDevicesAsync(default)).CurrentPowerW);
        Assert.Equal(binding.Id, Assert.Single((await gateway.ReadInventoryAsync(false, default)).Devices).Id.Value);
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewerPendingOrCancelledReaderCannotReplaceFreshResultWithPreexistingCache(bool cancelNewer)
    {
        var binding = Binding("Socket");
        var olderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOlder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNewer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        var executor = new Executor(async (_, _, p, ct) =>
        {
            var read = Interlocked.Increment(ref reads);
            if (read == 2) { olderStarted.SetResult(); await releaseOlder.Task.WaitAsync(ct); }
            if (read == 3) { newerStarted.SetResult(); await releaseNewer.Task.WaitAsync(ct); }
            return IntegrationJson.Element(new ProviderSocketTelemetry(p.GetProperty("remoteId").GetString()!, "0", true, true,
                read * 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        });
        var gateway = new DynamicSocketGateway(new Registry([binding], _ => Task.FromResult<IReadOnlyList<IntegrationDeviceBindingEntity>>([binding])), executor, new UnusedFactory(), TimeProvider.System);
        var snapshot = new DeviceStatusSnapshot();
        async Task<IReadOnlyList<DevicePowerInfo>> ReadAndPublishAsync(bool refresh, CancellationToken ct)
        {
            var epoch = snapshot.Epoch;
            var devices = refresh ? await gateway.RefreshDevicesAsync(ct) : await gateway.GetCachedDevicesAsync(ct);
            Assert.True(snapshot.TryUpdate(devices, epoch));
            return devices;
        }
        Assert.Equal(100, Assert.Single(await ReadAndPublishAsync(true, default)).CurrentPowerW);
        var earlier = ReadAndPublishAsync(true, default);
        await olderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newer = ReadAndPublishAsync(true, cancellation.Token);
        await newerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (cancelNewer)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => newer);
        }
        releaseOlder.SetResult();
        Assert.Equal(200, Assert.Single(await earlier).CurrentPowerW);
        Assert.Equal(200, Assert.Single(snapshot.Current!).CurrentPowerW);
        await ReadAndPublishAsync(false, default);
        Assert.Equal(200, Assert.Single(snapshot.Current!).CurrentPowerW);
        if (!cancelNewer)
        {
            releaseNewer.SetResult();
            Assert.Equal(300, Assert.Single(await newer).CurrentPowerW);
            await ReadAndPublishAsync(false, default);
            Assert.Equal(300, Assert.Single(snapshot.Current!).CurrentPowerW);
        }
        Assert.Equal(3, reads);
    }

    [SqlServerFact]
    public async Task ConcurrentInventoryReadersReturnValidSnapshotsWithoutChangingConnectionsOrNeighborData()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync("GatewayInventoryRace", SqlTestSchema.Model);
        var options = database.Options;
        var factory = new Factory(options, "a");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var owned = Guid.NewGuid();
        var executor = new Executor(async (session, method, parameters, ct) =>
        {
            Assert.Equal("a", session.InstallationId);
            Assert.Equal(owned, session.InstanceId);
            Assert.Equal("socket.read", method);
            Assert.Equal("same-remote", parameters.GetProperty("remoteId").GetString());
            var earlier = Interlocked.Increment(ref reads) == 1;
            if (earlier) { started.SetResult(); await release.Task.WaitAsync(ct); }
            return IntegrationJson.Element(new ProviderSocketTelemetry("same-remote", "0", !earlier, true,
                earlier ? 100 : 200, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        });
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                db.Installations.AddRange(new Installation { Id = "a" }, new Installation { Id = "b" });
                await db.SaveChangesAsync();
            }
            foreach (var site in new[] { "a", "b" })
            {
                await using var db = new Factory(options, site).CreateDbContext();
                var instance = site == "a" ? owned : Guid.NewGuid();
                var binding = Guid.NewGuid();
                db.IntegrationInstances.Add(new() { Id = instance, ProviderId = "test.provider", PackageVersion = "1.0.0", PackageDigest = "fixture", State = "enabled" });
                db.IntegrationConfigurations.Add(new() { InstanceId = instance, Revision = 1 });
                db.IntegrationDeviceBindings.Add(new() { Id = binding, InstanceId = instance, RemoteId = "same-remote", Channel = "0", Kind = "socket", MetadataJson = "{\"capabilities\":{\"canSwitch\":true}}" });
                db.TriggerRules.Add(new() { EntityId = binding.ToString("D"), Name = site, CurrentState = site == "b" });
                db.AppSettings.Add(new() { Section = "DeviceLabels", Key = "LabelsJson", Value = "{}" });
                await db.SaveChangesAsync();
            }
            var before = await InventoryDatabaseStateAsync(options);
            var registry = new IntegrationRegistry(factory, new IntegrationSecretStore(new EphemeralDataProtectionProvider()));
            var gateway = new DynamicSocketGateway(registry, executor, factory, TimeProvider.System);
            var snapshot = new DeviceStatusSnapshot();
            var neighbor = new DeviceStatusSnapshot();
            neighbor.Update([new("neighbor", "Neighbor", "Socket", true, true, 900)]);
            async Task<IReadOnlyList<DevicePowerInfo>> RefreshAndPublishAsync()
            {
                var epoch = snapshot.Epoch;
                var devices = await gateway.RefreshDevicesAsync(default);
                Assert.True(snapshot.TryUpdate(devices, epoch));
                return devices;
            }
            var earlier = RefreshAndPublishAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var later = await RefreshAndPublishAsync();
            Assert.Equal(200, Assert.Single(later).CurrentPowerW);
            release.SetResult();
            Assert.Equal(200, Assert.Single(await earlier).CurrentPowerW);
            Assert.Equal(200, Assert.Single(snapshot.Current!).CurrentPowerW);
            Assert.Equal(900, Assert.Single(neighbor.Current!).CurrentPowerW);
            Assert.Equal(200, Assert.Single(await gateway.GetCachedDevicesAsync(default)).CurrentPowerW);
            Assert.Equal(Reachability.Online, Assert.Single((await gateway.ReadInventoryAsync(false, default)).Devices).Reachability);
            Assert.Equal(2, reads);
            Assert.Equal(before, await InventoryDatabaseStateAsync(options));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static async Task<string> InventoryDatabaseStateAsync(DbContextOptions<DeyeSolarDbContext> options)
    {
        await using var db = new DeyeSolarDbContext(options);
        var state = new
        {
            Instances = await db.IntegrationInstances.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Configurations = await db.IntegrationConfigurations.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.InstanceId).ThenBy(row => row.Revision).ToArrayAsync(),
            Bindings = await db.IntegrationDeviceBindings.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Rules = await db.TriggerRules.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Settings = await db.AppSettings.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Commands = await db.IntegrationCommands.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()
        };
        Assert.Equal(2, state.Instances.Length);
        Assert.Equal(2, state.Configurations.Length);
        Assert.Equal(2, state.Bindings.Length);
        Assert.Equal(2, state.Rules.Length);
        Assert.Equal(2, state.Settings.Length);
        Assert.Equal(new[] { "a", "b" }, state.Instances.Select(row => row.InstallationId).OrderBy(id => id));
        Assert.False(Assert.Single(state.Rules, row => row.InstallationId == "a").CurrentState);
        Assert.True(Assert.Single(state.Rules, row => row.InstallationId == "b").CurrentState);
        Assert.Empty(state.Commands);
        return JsonSerializer.Serialize(state);
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

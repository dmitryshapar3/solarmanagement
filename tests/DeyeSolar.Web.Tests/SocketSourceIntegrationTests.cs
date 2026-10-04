using System.Security.Claims;
using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Workers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Inverters.Contracts;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Tests;

public class SocketSourceIntegrationTests
{
    [Fact]
    public async Task MixedProvidersShareOneSourcePreserveCapabilitiesAndRespectExplicitRuleOverride()
    {
        await using var f = await Fixture.CreateAsync();
        var shelly = await f.LinkAsync(f.Shelly, f.Secondary, 3);
        var tuya = await f.LinkAsync(f.Tuya, f.Secondary, 1);
        Assert.Equal(3, shelly.PhaseCount); Assert.Equal(1, tuya.PhaseCount);
        Assert.Equal(f.Secondary, shelly.SourceInverterId); Assert.Equal(f.Secondary, tuya.SourceInverterId);
        await using var db = f.Factory("a").CreateDbContext();
        db.IntegrationDeviceAliases.Add(new() { LegacyId = "legacy-tuya", DeviceId = f.Tuya });
        db.TriggerRules.AddRange(new TriggerRule { Name = "Shelly inherited", EntityId = f.Shelly.ToString("D") },
            new TriggerRule { Name = "Tuya inherited", EntityId = f.Tuya.ToString("D") },
            new TriggerRule { Name = "Explicit override", EntityId = f.Shelly.ToString("D"), SourceInverterId = f.Primary },
            new TriggerRule { Name = "Legacy alias", EntityId = "legacy-tuya" });
        await db.SaveChangesAsync();
        var rules = await db.TriggerRules.AsNoTracking().ToListAsync();
        var sources = await IntegrationSocketAssociation.ResolveSourcesAsync(db, rules, default);
        foreach (var rule in rules)
            Assert.Equal(rule.Name == "Explicit override" ? f.Primary : f.Secondary, sources[rule.Id]);
        var binding = await db.IntegrationDeviceBindings.SingleAsync(b => b.Id == f.Shelly);
        using var metadata = JsonDocument.Parse(binding.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("capabilities").GetProperty("canSwitch").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("capabilities").GetProperty("canMeasurePower").GetBoolean());
        Assert.Equal("kept", metadata.RootElement.GetProperty("publicNote").GetString());
        Assert.Equal(0, f.Executor.Sends);
    }

    [Fact]
    public async Task TenantSourcesAndActorsCannotCrossInstallationBoundary()
    {
        await using var f = await Fixture.CreateAsync();
        var options = await f.Service("a").SocketSourceOptionsAsync(default);
        Assert.Equal(new[] { f.Primary, f.Secondary }.Order(), options.Select(item => item.Id).Order());
        var request = await f.ChangeAsync(f.Shelly, f.ForeignSource);
        var foreignSource = await Assert.ThrowsAsync<IntegrationRequestException>(() => f.Service("a")
            .SetSocketSourceAsync(request.Instance, f.Shelly, request.Change, f.Actor("a"), default));
        Assert.Equal("invalid_source", foreignSource.Code);
        var foreignActor = await Assert.ThrowsAsync<IntegrationRequestException>(() => f.Service("a")
            .SetSocketSourceAsync(request.Instance, f.Shelly, request.Change with { SourceInverterId = f.Secondary }, f.Actor("b"), default));
        Assert.Equal("forbidden", foreignActor.Code);
        var foreignDevice = await Assert.ThrowsAsync<IntegrationRequestException>(() => f.Service("a")
            .SetSocketSourceAsync(request.Instance, f.ForeignSocket, request.Change with { SourceInverterId = f.Secondary }, f.Actor("a"), default));
        Assert.Equal("device_not_found", foreignDevice.Code);
        await using var db = f.Factory("b").CreateDbContext();
        Assert.Null(IntegrationSocketAssociation.Read(await db.IntegrationDeviceBindings.SingleAsync(b => b.Id == f.ForeignSocket)).SourceInverterId);
        Assert.Equal(0, f.Executor.Sends);
    }

    [Fact]
    public async Task StaleLinkAndUnsupportedPhaseChangesFailWithoutLosingSavedAssociation()
    {
        await using var f = await Fixture.CreateAsync();
        var stale = await f.ChangeAsync(f.Shelly, f.Secondary, 3);
        await f.Service("a").SetSocketSourceAsync(stale.Instance, f.Shelly, stale.Change, f.Actor("a"), default);
        var conflict = await Assert.ThrowsAsync<IntegrationRequestException>(() => f.Service("a")
            .SetSocketSourceAsync(stale.Instance, f.Shelly, stale.Change with { SourceInverterId = f.Primary }, f.Actor("a"), default));
        Assert.Equal("association_conflict", conflict.Code);
        var invalid = await f.ChangeAsync(f.Shelly, f.Primary, 2);
        Assert.Equal("validation", (await Assert.ThrowsAsync<IntegrationRequestException>(() => f.Service("a")
            .SetSocketSourceAsync(invalid.Instance, f.Shelly, invalid.Change, f.Actor("a"), default))).Code);
        var retained = await f.BindingAsync(f.Shelly);
        Assert.Equal(new IntegrationSocketAssociation.Association(f.Secondary, 3), IntegrationSocketAssociation.Read(retained));
        await f.LinkAsync(f.Shelly, null, 1);
        Assert.Equal(new IntegrationSocketAssociation.Association(null, 1), IntegrationSocketAssociation.Read(await f.BindingAsync(f.Shelly)));
        await using var check = f.Factory("a").CreateDbContext();
        var inherited = new TriggerRule { Id = 100, EntityId = f.Shelly.ToString("D") };
        Assert.Null((await IntegrationSocketAssociation.ResolveSourcesAsync(check, [inherited], default))[inherited.Id]);
    }

    [Fact]
    public async Task InheritedPvRulesRequireSolarCapabilityWhileDisabledAndExplicitRulesDoNotBlockLinking()
    {
        await using var f = await Fixture.CreateAsync();
        await f.LinkAsync(f.Shelly, f.Primary);
        var repository = new RuleRepository(f.Factory("a"));
        var inherited = await repository.CreateAsync(new() { Name = "Inherited PV", EntityId = f.Shelly.ToString("D"), UseSolarProductionThreshold = true }, default);
        await using (var db = f.Factory("a").CreateDbContext())
        {
            (await db.IntegrationDeviceBindings.SingleAsync(binding => binding.Id == f.Secondary)).MetadataJson
                = "{\"capabilities\":{\"hasBattery\":true,\"hasSolarPower\":false}}";
            await db.SaveChangesAsync();
        }
        var blocked = await Assert.ThrowsAsync<IntegrationRequestException>(() => f.LinkAsync(f.Shelly, f.Secondary));
        Assert.Equal("invalid_source", blocked.Code);
        Assert.Equal(f.Primary, IntegrationSocketAssociation.Read(await f.BindingAsync(f.Shelly)).SourceInverterId);
        inherited.Enabled = false;
        await repository.UpdateAsync(inherited, default);
        Assert.Equal(f.Secondary, (await f.LinkAsync(f.Shelly, f.Secondary)).SourceInverterId);
        inherited.Enabled = true;
        await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateAsync(inherited, default));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateAsync(new() { Name = "New inherited PV",
            EntityId = f.Shelly.ToString("D"), UseSolarProductionThreshold = true }, default));
        await repository.CreateAsync(new() { Name = "Explicit PV", EntityId = f.Shelly.ToString("D"),
            SourceInverterId = f.Primary, UseSolarProductionThreshold = true }, default);
        await f.LinkAsync(f.Shelly, f.Primary);
        Assert.Equal(f.Secondary, (await f.LinkAsync(f.Shelly, f.Secondary)).SourceInverterId);
        Assert.Equal(0, f.Executor.Sends);
    }

    [Fact]
    public async Task PendingCommandProtectsSourceButNotAnotherProviderInstance()
    {
        await using var f = await Fixture.CreateAsync();
        f.Executor.Pending = true;
        var gateway = f.Sockets("a");
        var socket = await gateway.GetAsync(new(f.Shelly), default);
        Assert.Equal(SocketCommandStatus.Pending, (await socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.On), default)).Status);
        var change = await f.ChangeAsync(f.Shelly, f.Secondary);
        Assert.Equal("active_commands", (await Assert.ThrowsAsync<IntegrationRequestException>(() => f.Service("a")
            .SetSocketSourceAsync(change.Instance, f.Shelly, change.Change, f.Actor("a"), default))).Code);
        var another = await f.LinkAsync(f.Tuya, f.Secondary, 3);
        Assert.Equal(f.Secondary, another.SourceInverterId);
        Assert.Null(IntegrationSocketAssociation.Read(await f.BindingAsync(f.Shelly)).SourceInverterId);
        Assert.Equal(1, f.Executor.Sends);
    }

    [Fact]
    public async Task SourceChangedAfterEvaluationCannotInsertIntentAndManualCommandStillWorks()
    {
        await using var f = await Fixture.CreateAsync();
        await f.LinkAsync(f.Shelly, f.Secondary);
        // Simulate evaluation using the old source, followed by a saved link change
        // before the gateway obtains its fresh runtime session.
        using (IntegrationAutomationSourceGuard.Enter(f.Secondary))
        {
            await f.LinkAsync(f.Shelly, f.Primary);
            var socket = await f.Sockets("a").GetAsync(new(f.Shelly), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.On), default));
        }
        await using (var db = f.Factory("a").CreateDbContext()) Assert.Empty(await db.IntegrationCommands.ToListAsync());
        Assert.Equal(0, f.Executor.Sends);
        var manual = await f.Sockets("a").GetAsync(new(f.Shelly), default);
        Assert.Equal(SocketCommandStatus.Acknowledged, (await manual.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.On), default)).Status);
        Assert.Equal(1, f.Executor.Sends);
    }

    [Fact]
    public async Task ConcurrentExplicitRuleEditIsRejectedAndAnUnchangedOverrideIgnoresSocketLink()
    {
        await using var f = await Fixture.CreateAsync();
        await f.LinkAsync(f.Shelly, f.Secondary);
        var repository = new RuleRepository(f.Factory("a"));
        var rule = await repository.CreateAsync(new() { Name = "Explicit", EntityId = f.Shelly.ToString("D"), SourceInverterId = f.Primary }, default);
        using (IntegrationAutomationSourceGuard.Enter(f.Primary, rule))
        {
            var socket = await f.Sockets("a").GetAsync(new(f.Shelly), default);
            Assert.Equal(SocketCommandStatus.Acknowledged, (await socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.On), default)).Status);
        }
        await using (var db = f.Factory("a").CreateDbContext())
        {
            var current = await db.TriggerRules.SingleAsync(); current.SourceInverterId = f.Secondary;
            await db.SaveChangesAsync();
        }
        using (IntegrationAutomationSourceGuard.Enter(f.Primary, rule))
        {
            var socket = await f.Sockets("a").GetAsync(new(f.Shelly), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.Off), default));
        }
        Assert.Equal(1, f.Executor.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceGenerationOrDefaultSelectionChangedAfterObservationCannotInsertIntent(bool defaultSelection)
    {
        await using var f = await Fixture.CreateAsync();
        var sourceId = defaultSelection ? f.Primary : f.Secondary;
        if (!defaultSelection) await f.LinkAsync(f.Shelly, sourceId);
        var registry = new IntegrationRegistry(f.Factory("a"), f.Secrets);
        var sources = new DynamicInverterGateway(registry, f.Executor, new(registry), TimeProvider.System);
        var observation = await sources.ReadDeviceAsync(new(sourceId), default);
        await using (var db = f.Factory("a").CreateDbContext())
        {
            var source = await db.IntegrationDeviceBindings.SingleAsync(binding => binding.Id == sourceId);
            if (defaultSelection)
            {
                source.IsDefault = false;
                // Release the unique default-inverter slot before promoting another binding.
                await db.SaveChangesAsync();
                (await db.IntegrationDeviceBindings.SingleAsync(binding => binding.Id == f.Secondary)).IsDefault = true;
            }
            else (await db.IntegrationInstances.SingleAsync(instance => instance.Id == source.InstanceId)).Generation++;
            await db.SaveChangesAsync();
        }
        using (IntegrationAutomationSourceGuard.Enter(defaultSelection ? null : sourceId, observation: observation))
        {
            var socket = await f.Sockets("a").GetAsync(new(f.Shelly), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => socket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.On), default));
        }
        await using var check = f.Factory("a").CreateDbContext();
        Assert.Empty(await check.IntegrationCommands.ToListAsync());
        Assert.Equal(0, f.Executor.Sends);
    }

    [Fact]
    public async Task PollingUsesLinkedSecondarySocForBothMixedProviderSockets()
    {
        await using var f = await Fixture.CreateAsync();
        await f.LinkAsync(f.Shelly, f.Secondary, 3);
        await f.LinkAsync(f.Tuya, f.Secondary, 1);
        var repository = new RuleRepository(f.Factory("a"));
        await repository.CreateAsync(new() { Name = "Shelly linked", EntityId = f.Shelly.ToString("D"), SocTurnOnThreshold = 80 }, default);
        await repository.CreateAsync(new() { Name = "Tuya linked", EntityId = f.Tuya.ToString("D"), SocTurnOnThreshold = 80 }, default);
        var registry = new IntegrationRegistry(f.Factory("a"), f.Secrets);
        var source = new DynamicInverterGateway(registry, f.Executor, new(registry), TimeProvider.System);
        var readings = new ExportReadingStore(f.Factory("a"), TimeProvider.System);
        using var worker = new PollingWorker(new Refresh(ct => source.ReadDeviceAsync(new(f.Primary), ct)),
            new Monitor<InverterConnectionOptions>(new() { DeviceKey = f.Primary.ToString("D") }), f.Sockets("a"), repository,
            new RuleEvaluator(), f.Factory("a"), new Monitor<PollingOptions>(new()),
            new AppSettingsService(f.Factory("a"), new ConfigurationBuilder().Build()), NullLogger<PollingWorker>.Instance, source, readings);
        await worker.PollAndEvaluateAsync(default);
        await using var check = f.Factory("a").CreateDbContext();
        var commands = await check.IntegrationCommands.ToListAsync();
        Assert.Equal(new[] { f.Shelly, f.Tuya }.Order(), commands.Select(command => command.DeviceId).Order());
        Assert.All(commands, command => { Assert.Equal("acknowledged", command.Status); Assert.True(command.DesiredState); });
        Assert.All(await check.TriggerRules.ToListAsync(), rule => Assert.True(rule.CurrentState));
        Assert.Equal(2, f.Executor.Sends);
        Assert.Equal(90, (await check.Readings.SingleAsync()).BatterySoc);
        Assert.Empty(await check.IntegrationCommands.IgnoreQueryFilters().Where(command => command.InstallationId == "b").ToListAsync());
    }

    private sealed class SqliteModelContext(DbContextOptions<DeyeSolarDbContext> options) : DeyeSolarDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Installation>().Property(i => i.CreatedAt).HasConversion<long>();
        }
    }

    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options, string installation) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options, installation);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private DbContextOptions<DeyeSolarDbContext> _options = null!;
        private readonly EphemeralDataProtectionProvider _protection = new();
        public IntegrationSecretStore Secrets { get; }
        public Executor Executor { get; } = new();
        public Guid Primary { get; } = Guid.NewGuid();
        public Guid Secondary { get; } = Guid.NewGuid();
        public Guid Shelly { get; } = Guid.NewGuid();
        public Guid Tuya { get; } = Guid.NewGuid();
        public Guid ForeignSource { get; } = Guid.NewGuid();
        public Guid ForeignSocket { get; } = Guid.NewGuid();
        private Fixture() => Secrets = new(_protection);
        public Factory Factory(string installation) => new(_options, installation);
        public ClaimsPrincipal Actor(string installation) => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "owner-" + installation), new Claim(InstallationIds.ClaimType, installation)], "fixture"));
        public IntegrationSetupService Service(string installation)
        {
            var current = new CurrentInstallation(); current.BindOnce(installation);
            var factory = Factory(installation);
            return new(factory, new UnusedSetup(), new UnusedSetup(), Secrets, _protection, TimeProvider.System,
                new(factory), current, new(NullLogger<IntegrationChangeNotifier>.Instance), new());
        }
        public DynamicSocketGateway Sockets(string installation) => new(new IntegrationRegistry(Factory(installation), Secrets), Executor, Factory(installation), TimeProvider.System);
        public async Task<IntegrationDeviceBindingEntity> BindingAsync(Guid device)
        {
            await using var db = Factory("a").CreateDbContext();
            return await db.IntegrationDeviceBindings.AsNoTracking().SingleAsync(binding => binding.Id == device);
        }
        public async Task<(Guid Instance, IntegrationSocketSourceChange Change)> ChangeAsync(Guid device, Guid? source, int phase = 1)
        {
            var binding = await BindingAsync(device);
            await using var db = Factory("a").CreateDbContext();
            var instance = await db.IntegrationInstances.SingleAsync(instance => instance.Id == binding.InstanceId);
            var previous = IntegrationSocketAssociation.Read(binding);
            return (instance.Id, new(new(instance.Revision, instance.PackageVersion, instance.PackageDigest, instance.DescriptorDigest),
                source, phase, previous.SourceInverterId, previous.PhaseCount));
        }
        public async Task<IntegrationBindingDto> LinkAsync(Guid device, Guid? source, int phase = 1)
        {
            var request = await ChangeAsync(device, source, phase);
            return await Service("a").SetSocketSourceAsync(request.Instance, device, request.Change, Actor("a"), default);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); await f._connection.OpenAsync();
            f._connection.CreateCollation("Latin1_General_100_BIN2", string.CompareOrdinal);
            var initial = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(f._connection).Options;
            await using (var model = new SqliteModelContext(initial))
            {
                await model.Database.EnsureCreatedAsync();
                f._options = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlite(f._connection).UseModel(model.Model).Options;
            }
            await using (var db = f.Factory("a").CreateDbContext())
            {
                foreach (var tenant in new[] { "a", "b" })
                {
                    db.Installations.Add(new() { Id = tenant, CreatedAt = DateTimeOffset.UtcNow });
                    db.Users.Add(new IdentityUser { Id = "owner-" + tenant, UserName = "owner-" + tenant, NormalizedUserName = "OWNER-" + tenant });
                    db.InstallationMemberships.Add(new() { UserId = "owner-" + tenant, InstallationId = tenant, Role = "Owner" });
                }
                await db.SaveChangesAsync();
            }
            await f.SeedAsync("a", f.Primary, "inverter", "deye.cloud", "primary", true);
            await f.SeedAsync("a", f.Secondary, "inverter", "solis.cloud", "secondary");
            await f.SeedAsync("a", f.Shelly, "socket", "shelly.cloud", "shared-remote");
            await f.SeedAsync("a", f.Tuya, "socket", "tuya.cloud", "shared-remote");
            await f.SeedAsync("b", f.ForeignSource, "inverter", "growatt.cloud", "foreign", true);
            await f.SeedAsync("b", f.ForeignSocket, "socket", "shelly.cloud", "shared-remote");
            return f;
        }
        private async Task SeedAsync(string tenant, Guid device, string kind, string provider, string remote, bool isDefault = false)
        {
            await using var db = Factory(tenant).CreateDbContext();
            var instance = new IntegrationInstanceEntity { Id = Guid.NewGuid(), ProviderId = provider, Name = provider, PackageVersion = "1.0.0",
                PackageDigest = "fixture-digest", DescriptorDigest = "fixture-descriptor", ConfigurationVersion = 1, State = "enabled" };
            db.Add(instance); db.Add(new IntegrationConfigurationEntity { InstanceId = instance.Id, Revision = 1 });
            db.Add(new IntegrationDeviceBindingEntity { Id = device, InstanceId = instance.Id, Kind = kind, RemoteId = remote,
                Channel = kind == "socket" ? "0" : "", Name = provider, IsDefault = isDefault,
                MetadataJson = kind == "socket" ? "{\"capabilities\":{\"canSwitch\":true,\"canMeasurePower\":true},\"publicNote\":\"kept\"}"
                    : "{\"capabilities\":{\"hasBattery\":true,\"hasSolarPower\":true,\"solarBasis\":\"PvDc\"}}" });
            await db.SaveChangesAsync();
        }
        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed class Executor : IIntegrationRuntimeExecutor
    {
        public int Sends; public bool Pending;
        public Task StopAsync(Guid instance, CancellationToken ct) => Task.CompletedTask;
        public Task<JsonElement> InvokeAsync(IntegrationSession session, string method, JsonElement parameters, CancellationToken ct)
        {
            if (method == "socket.set")
            {
                Sends++;
                return Task.FromResult(IntegrationJson.Element(new ProviderSocketCommandResult(parameters.GetProperty("commandId").GetString()!,
                    Pending ? "Pending" : "Acknowledged", Pending ? "fixture-operation" : null)));
            }
            if (method != "inverter.read") throw new InvalidOperationException("Unexpected fixture operation.");
            var remote = parameters.GetProperty("remoteId").GetString()!;
            var observed = DateTimeOffset.UtcNow.AddSeconds(-2);
            var missing = new ProviderMeasurement(null, null, ProviderMeasurementQuality.Missing);
            var zero = new ProviderMeasurement(0, observed, ProviderMeasurementQuality.Good);
            // No grid measurement avoids the SQL Server-specific export upsert; the
            // actual SOC source, reading persistence and command transactions are exercised.
            return Task.FromResult(IntegrationJson.Element(new ProviderInverterTelemetry(remote, DateTimeOffset.UtcNow, "PvDc",
                new(remote == "secondary" ? 90 : 20, observed, ProviderMeasurementQuality.Good), missing, missing, missing, missing, zero, missing, zero)));
        }
    }

    private sealed class UnusedSetup : IIntegrationProviderCatalog, IIntegrationSetupExecutor
    {
        public Task<IntegrationProviderDescriptor> GetAsync(string id, string? version, CancellationToken ct) => throw new InvalidOperationException();
        public Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<SolarManagement.Integrations.Contracts.IntegrationTestResult> TestAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, CancellationToken ct) => throw new InvalidOperationException();
        public Task<IReadOnlyList<IntegrationDiscoveredDevice>> DiscoverAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft,
            IntegrationDiscoveryQuery query, CancellationToken ct) => throw new InvalidOperationException();
    }
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value; public T Get(string? name) => value; public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    private sealed class Refresh(Func<CancellationToken, Task<InverterData>> read) : IInverterRefreshService
    {
        public Task<InverterData> RefreshAsync(CancellationToken ct) => read(ct);
    }
}

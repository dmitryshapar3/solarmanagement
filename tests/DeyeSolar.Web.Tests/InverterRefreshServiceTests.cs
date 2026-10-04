using SolarManagement.Inverters.Contracts;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.RuleEngine;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Workers;
using DeyeSolar.Web.Integrations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tests;

public class InverterRefreshServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task MissingDeviceAndPreCanceledCallsHaveNoExternalEffects()
    {
        var source = new Source(_ => throw new InvalidOperationException("Source must not run."));
        var options = new Monitor<InverterConnectionOptions>(new());
        await using var service = Create(source, new RejectingFactory(), new(), options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync(default));
        options.CurrentValue.DeviceKey = "selected";
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(cancel.Token));
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task FailedSourceRetainsLastSnapshotAndCanBeRetried()
    {
        var old = Data(-100);
        var snapshot = new InverterDataSnapshot();
        snapshot.Update(old);
        var source = new Source(_ => throw new InvalidDataException("Unavailable observation."));
        await using var service = Create(source, new RejectingFactory(), snapshot);
        for (var attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsAsync<InvalidDataException>(() => service.RefreshAsync(default));
        Assert.Same(old, snapshot.Current);
        Assert.Equal(2, source.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedConfigurationAndForeignProvenanceNeverReachPersistence(bool changeConfiguration)
    {
        var response = Gate<InverterData>();
        var started = Gate<bool>();
        var source = new Source(_ => { started.TrySetResult(true); return response.Task; });
        var old = Data(-100);
        var snapshot = new InverterDataSnapshot();
        snapshot.Update(old);
        var options = new Monitor<InverterConnectionOptions>(new() { DeviceKey = "selected", ConnectionIdentity = "original-account" });
        await using var service = Create(source, new RejectingFactory(), snapshot, options);
        var read = service.RefreshAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (changeConfiguration)
            options.Change(new() { DeviceKey = "selected", ConnectionIdentity = "replacement-account" });
        response.SetResult(Data(-200, changeConfiguration ? "selected" : "neighbor"));
        if (changeConfiguration) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => read);
        Assert.Same(old, snapshot.Current);
    }

    [Fact]
    public async Task LastCanceledWaiterAbandonsWorkAndNextRefreshStartsIndependently()
    {
        var started = Gate<bool>();
        var abandoned = Gate<bool>();
        var releaseOld = Gate<InverterData>();
        var calls = 0;
        var source = new Source(ct =>
        {
            if (Interlocked.Increment(ref calls) > 1) throw new InvalidDataException("New independent request.");
            ct.Register(() => abandoned.TrySetResult(true));
            started.TrySetResult(true);
            return releaseOld.Task;
        });
        var snapshot = new InverterDataSnapshot();
        await using var service = Create(source, new RejectingFactory(), snapshot);
        using var cancel = new CancellationTokenSource();
        var first = service.RefreshAsync(cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RefreshAsync(default));
        releaseOld.SetResult(Data(-200));
        await service.DisposeAsync();
        Assert.Equal(2, source.Calls);
        Assert.Null(snapshot.Current);
    }

    [Fact]
    public async Task DisposeCancelsAndDrainsActiveReadWithoutPublishingItsLateResponse()
    {
        var started = Gate<bool>();
        var canceled = Gate<bool>();
        var release = Gate<InverterData>();
        var source = new Source(ct =>
        {
            ct.Register(() => canceled.TrySetResult(true));
            started.SetResult(true);
            return release.Task;
        });
        var snapshot = new InverterDataSnapshot();
        var service = Create(source, new RejectingFactory(), snapshot);
        var read = service.RefreshAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = service.DisposeAsync().AsTask();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(dispose.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.RefreshAsync(default));
        release.SetResult(Data(-200));
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Null(snapshot.Current);
        service.Dispose();
    }

    [SqlServerFact]
    public async Task ConcurrentCallersShareOneFetchAndCommitManualReadWithoutRuleEffects()
    {
        await using var database = await Database.CreateAsync();
        await database.Store.UpsertHistoryAsync("neighbor", [new(Now.AddMinutes(-1), 8765)], Now, default);
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.TriggerRules.Add(new() { Name = "manual refresh must not execute", EntityId = "socket-neighbor", SocTurnOnThreshold = 50 });
            seed.AppSettings.Add(new() { Section = "Neighbor", Key = "preserved", Value = "untouched" });
            await seed.SaveChangesAsync();
        }
        var release = Gate<InverterData>();
        var started = Gate<bool>();
        var source = new Source(_ => { started.TrySetResult(true); return release.Task; });
        var snapshot = new InverterDataSnapshot();
        var notifications = 0;
        snapshot.OnDataUpdated += () =>
        {
            using var committed = database.Factory.CreateDbContext();
            Assert.Equal(1, committed.Readings.Count());
            Assert.Equal(-2500, committed.ExportReadings.Single(row => row.DeviceSn == "selected").GridPowerWatts);
            notifications++;
        };
        await using var service = Create(source, database.Factory, snapshot);
        using var firstCaller = new CancellationTokenSource();
        var first = service.RefreshAsync(firstCaller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.RefreshAsync(default);
        firstCaller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var data = Data(-2500);
        release.SetResult(data);
        Assert.Same(data, await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(data, snapshot.Current);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, notifications);
        await using var check = database.Factory.CreateDbContext();
        Assert.Single(await check.Readings.ToListAsync());
        Assert.Equal(2, await check.ExportReadings.CountAsync());
        Assert.Equal(8765, (await check.ExportReadings.SingleAsync(row => row.DeviceSn == "neighbor")).GridPowerWatts);
        var rule = await check.TriggerRules.SingleAsync();
        Assert.False(rule.CurrentState);
        Assert.Null(rule.LastEvaluated);
        Assert.Empty(await check.RuleRunLogs.ToListAsync());
        Assert.Equal("untouched", (await check.AppSettings.SingleAsync()).Value);
    }

    [SqlServerFact]
    public async Task FailedAtomicPersistenceRetainsSnapshotAndOnlyRollsBackItsOwnRows()
    {
        await using var database = await Database.CreateAsync();
        await database.Store.UpsertHistoryAsync("neighbor", [new(Now.AddMinutes(-1), 8765)], Now, default);
        await using (var setup = database.Factory.CreateDbContext())
            await setup.Database.ExecuteSqlRawAsync("ALTER TABLE Readings ADD CONSTRAINT CK_RefreshFailure CHECK (GridConsumption <> 778877);");
        var old = Data(-100);
        var snapshot = new InverterDataSnapshot();
        snapshot.Update(old);
        var source = new Source(_ => Task.FromResult(Data(778877)));
        await using var service = Create(source, database.Factory, snapshot);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.RefreshAsync(default));
        Assert.Same(old, snapshot.Current);
        await using var check = database.Factory.CreateDbContext();
        Assert.Empty(await check.Readings.ToListAsync());
        var retained = Assert.Single(await check.ExportReadings.ToListAsync());
        Assert.Equal("neighbor", retained.DeviceSn);
        Assert.Equal(8765, retained.GridPowerWatts);
    }

    [SqlServerFact]
    public async Task SubsequentRefreshFetchesAgainAndSubscriberFailureCannotUndoCommittedData()
    {
        await using var database = await Database.CreateAsync();
        var watts = -100;
        var source = new Source(_ => Task.FromResult(Data(watts -= 100)));
        var snapshot = new InverterDataSnapshot();
        snapshot.OnDataUpdated += () => throw new InvalidOperationException("Subscriber failed after commit.");
        await using var service = Create(source, database.Factory, snapshot);
        Assert.Equal(-200, (await service.RefreshAsync(default)).GridConsumption);
        Assert.Equal(-300, (await service.RefreshAsync(default)).GridConsumption);
        Assert.Equal(2, source.Calls);
        Assert.Equal(-300, snapshot.Current!.GridConsumption);
        await using var check = database.Factory.CreateDbContext();
        Assert.Equal(2, await check.Readings.CountAsync());
        Assert.Equal(-300, (await check.ExportReadings.SingleAsync()).GridPowerWatts);
    }

    [SqlServerFact]
    public async Task AutomaticPollingUsesSameCommitThenEvaluatesAndAuditsOnlyEligibleRules()
    {
        await using var database = await Database.CreateAsync();
        await database.Store.UpsertHistoryAsync("neighbor", [new(Now.AddDays(-90), 8765)], Now, default);
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.TriggerRules.AddRange(
                new TriggerRule { Name = "eligible", EntityId = "socket-selected", SocTurnOnThreshold = 50 },
                new TriggerRule { Name = "disabled neighbor", EntityId = "socket-neighbor", Enabled = false });
            seed.Readings.Add(new() { Timestamp = DateTime.UtcNow.AddDays(-32), GridConsumption = 9876 });
            await seed.SaveChangesAsync();
        }
        var options = new Monitor<InverterConnectionOptions>(new() { DeviceKey = "selected" });
        var source = new Source(_ => Task.FromResult(Data(-2000)));
        await using var refresh = Create(source, database.Factory, new(), options);
        await refresh.RefreshAsync(default);
        var socket = new Socket(database.Factory);
        using var worker = Worker(database.Factory, refresh, options, new RuleRepository(database.Factory), socket);
        await RunOneCycleAsync(worker);

        Assert.Equal(new[] { "socket-selected" }, socket.TurnedOn);
        Assert.Equal(2, source.Calls);
        await using var check = database.Factory.CreateDbContext();
        Assert.Equal(2, await check.Readings.CountAsync());
        Assert.DoesNotContain(await check.Readings.ToListAsync(), row => row.GridConsumption == 9876);
        Assert.Equal(2, await check.ExportReadings.CountAsync());
        Assert.Equal(8765, (await check.ExportReadings.SingleAsync(row => row.DeviceSn == "neighbor")).GridPowerWatts);
        var active = await check.TriggerRules.SingleAsync(rule => rule.Name == "eligible");
        Assert.True(active.CurrentState);
        Assert.NotNull(active.LastEvaluated);
        Assert.NotNull(active.CurrentStateChangedAt);
        var neighbor = await check.TriggerRules.SingleAsync(rule => rule.Name == "disabled neighbor");
        Assert.False(neighbor.CurrentState);
        Assert.Null(neighbor.LastEvaluated);
        Assert.Equal("ON", (await check.RuleRunLogs.SingleAsync()).Action);
    }

    [SqlServerFact]
    public async Task RegisteredOldSourceCannotTriggerPrimaryRulesAfterAnotherInstanceBecomesPrimary()
    {
        await using var database = await Database.CreateAsync();
        var instanceA = Guid.NewGuid(); var instanceB = Guid.NewGuid();
        var deviceA = Guid.NewGuid(); var deviceB = Guid.NewGuid();
        await using (var seed = database.Factory.CreateDbContext())
        {
            foreach (var (instance, device, primary) in new[] { (instanceA, deviceA, true), (instanceB, deviceB, false) })
            {
                seed.IntegrationInstances.Add(new() { Id = instance, ProviderId = "fixture.registered", Name = "Registered inverter",
                    PackageVersion = "1.0", PackageDigest = "fixture-package", DescriptorDigest = "fixture-ui", ConfigurationVersion = 1,
                    State = "enabled", CreatedAt = Now, UpdatedAt = Now });
                seed.IntegrationConfigurations.Add(new() { InstanceId = instance, Revision = 1, ValuesJson = "{}", CreatedAt = Now });
                seed.IntegrationDeviceBindings.Add(new() { Id = device, InstanceId = instance, Kind = "inverter", Name = "Inverter",
                    RemoteId = device.ToString("D"), IsDefault = primary, Enabled = true });
            }
            seed.TriggerRules.AddRange(new() { Name = "Primary rule", EntityId = "primary-socket", SocTurnOnThreshold = 50 },
                new() { Name = "Disabled neighbour", EntityId = "neighbour-socket", Enabled = false });
            await seed.SaveChangesAsync();
        }
        var options = new Monitor<InverterConnectionOptions>(new() { DeviceKey = deviceA.ToString("D"),
            ConnectionIdentity = instanceA.ToString("D"), Revision = 1, Generation = 1 });
        var observation = Data(-2000, deviceA.ToString("D")) with { InverterId = deviceA, ConfigurationRevision = 1, RuntimeGeneration = 1 };
        var source = new RegisteredSource(database.Factory, observation);
        await using var refresh = Create(source, database.Factory, new(), options);
        var rules = new AsyncChangingRules(new RuleRepository(database.Factory), async () =>
        {
            await using var db = database.Factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.IntegrationDeviceBindings.Where(binding => binding.IsDefault).ExecuteUpdateAsync(update => update.SetProperty(binding => binding.IsDefault, false));
            await db.IntegrationDeviceBindings.Where(binding => binding.Id == deviceB).ExecuteUpdateAsync(update => update.SetProperty(binding => binding.IsDefault, true));
            await transaction.CommitAsync();
            options.Change(new() { DeviceKey = deviceB.ToString("D"), ConnectionIdentity = instanceB.ToString("D"), Revision = 1, Generation = 1 });
        });
        var socket = new Socket();
        using var worker = PollingWorkerFixture.Create(refresh, options, socket, rules, new RuleEvaluator(), database.Factory,
            new Monitor<PollingOptions>(new()), new AppSettingsService(database.Factory, new ConfigurationBuilder().Build()),
            NullLogger<PollingWorker>.Instance, source, database.Store);
        await RunOneCycleAsync(worker);
        Assert.True(await source.IsCurrentAsync(observation, default)); // The old registered account remains valid for explicit source rules.
        Assert.Empty(socket.TurnedOn);
        await using var check = database.Factory.CreateDbContext();
        Assert.All(await check.TriggerRules.ToListAsync(), rule => { Assert.False(rule.CurrentState); Assert.Null(rule.LastEvaluated); });
        Assert.Empty(await check.RuleRunLogs.ToListAsync());
        Assert.Equal(deviceA, (await check.Readings.SingleAsync()).InverterId);
        Assert.Equal(deviceB, (await check.IntegrationDeviceBindings.SingleAsync(binding => binding.IsDefault)).Id);
    }

    [SqlServerFact]
    public async Task AccountChangeWhileLoadingRulesPreventsStaleRuleActionsAndBookkeeping()
    {
        await using var database = await Database.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.TriggerRules.Add(new() { Name = "eligible", EntityId = "socket-selected", SocTurnOnThreshold = 50 });
            await seed.SaveChangesAsync();
        }
        var options = new Monitor<InverterConnectionOptions>(new() { DeviceKey = "selected", ConnectionIdentity = "original-account" });
        var source = new Source(_ => Task.FromResult(Data(-2000)));
        await using var refresh = Create(source, database.Factory, new(), options);
        var rules = new ChangingRules(new RuleRepository(database.Factory), () => options.Change(
            new() { DeviceKey = "selected", ConnectionIdentity = "replacement-account" }));
        var socket = new Socket();
        using var worker = Worker(database.Factory, refresh, options, rules, socket);
        await RunOneCycleAsync(worker);
        Assert.Empty(socket.TurnedOn);
        await using var check = database.Factory.CreateDbContext();
        var rule = await check.TriggerRules.SingleAsync();
        Assert.False(rule.CurrentState);
        Assert.Null(rule.LastEvaluated);
        Assert.Empty(await check.RuleRunLogs.ToListAsync());
        Assert.Single(await check.Readings.ToListAsync());
    }

    private static PollingWorker Worker(Factory factory, IInverterRefreshService refresh,
        Monitor<InverterConnectionOptions> options, IRuleRepository rules, ISocketController socket) => PollingWorkerFixture.Create(
            refresh, options, socket, rules, new RuleEvaluator(), factory,
            new Monitor<PollingOptions>(new()), new AppSettingsService(factory, new ConfigurationBuilder().Build()),
            NullLogger<PollingWorker>.Instance);
    private static Task RunOneCycleAsync(PollingWorker worker) => (Task)typeof(PollingWorker)
        .GetMethod("PollAndEvaluateAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(worker, [CancellationToken.None])!;
    private sealed class Socket(Factory? factory = null) : ISocketController
    {
        public List<string> TurnedOn { get; } = [];
        public async Task TurnOnAsync(string entityId, CancellationToken ct)
        {
            TurnedOn.Add(entityId);
            if (factory is null) return;
            await using var db = factory.CreateDbContext();
            foreach (var rule in await db.TriggerRules.Where(rule => rule.EntityId == entityId).ToListAsync(ct))
            {
                if (!rule.CurrentState) rule.CurrentStateChangedAt = DateTime.UtcNow;
                rule.CurrentState = true;
            }
            await db.SaveChangesAsync(ct);
        }
        public Task TurnOffAsync(string entityId, CancellationToken ct) => throw new InvalidOperationException("Unexpected OFF action.");
        public Task<bool> GetStateAsync(string entityId, CancellationToken ct) => Task.FromResult(false);
    }
    private sealed class ChangingRules(IRuleRepository inner, Action change) : IRuleRepository
    {
        public async Task<List<TriggerRule>> GetAllAsync(CancellationToken ct)
        { var rules = await inner.GetAllAsync(ct); change(); return rules; }
        public Task<TriggerRule?> GetByIdAsync(int id, CancellationToken ct) => inner.GetByIdAsync(id, ct);
        public Task<TriggerRule> CreateAsync(TriggerRule rule, CancellationToken ct) => inner.CreateAsync(rule, ct);
        public Task UpdateAsync(TriggerRule rule, CancellationToken ct) => inner.UpdateAsync(rule, ct);
        public Task RecordEvaluationAsync(int ruleId, DateTime when, CancellationToken ct) => inner.RecordEvaluationAsync(ruleId, when, ct);
        public Task DeleteAsync(int id, string configurationVersion, CancellationToken ct) => inner.DeleteAsync(id, configurationVersion, ct);
    }

    private static InverterData Data(int watts, string device = "selected") => ConfirmedInverterReading.Create(new()
    {
        Timestamp = Now, GridConsumption = watts, GridObservedAt = Now.AddMinutes(-1), GridDeviceSn = device,
        SolarProduction = 3100, SolarObservedAt = Now.AddMinutes(-1), SolarDeviceSn = device, BatterySoc = 90
    });
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static InverterRefreshService Create(IInverterDataSource source, IDbContextFactory<DeyeSolarDbContext> factory,
        InverterDataSnapshot snapshot, Monitor<InverterConnectionOptions>? options = null) => new(source,
            new(factory, new Clock()), snapshot, options ?? new(new() { DeviceKey = "selected" }),
            new Lifetime(), NullLogger<InverterRefreshService>.Instance);
    private sealed class Source(Func<CancellationToken, Task<InverterData>> read) : IInverterDataSource
    {
        private int _calls;
        public int Calls => _calls;
        public Task<InverterData> ReadCurrentDataAsync(CancellationToken ct) { Interlocked.Increment(ref _calls); return read(ct); }
    }
    private sealed class RegisteredSource(Factory factory, InverterData observation) : IInverterDataSource, IRegisteredInverterDataSource
    {
        public Task<InverterData> ReadCurrentDataAsync(CancellationToken ct) => Task.FromResult(observation);
        public Task<InverterData> ReadDeviceAsync(InverterId id, CancellationToken ct) => throw new InvalidOperationException("Only the captured primary is requested by this fixture.");
        public async Task<bool> IsCurrentAsync(InverterData data, CancellationToken ct)
        {
            await using var db = factory.CreateDbContext();
            return await (from binding in db.IntegrationDeviceBindings join instance in db.IntegrationInstances on binding.InstanceId equals instance.Id
                where binding.Id == data.InverterId && binding.Enabled && instance.State == "enabled"
                    && instance.Revision == data.ConfigurationRevision && instance.Generation == data.RuntimeGeneration select binding.Id).AnyAsync(ct);
        }
    }
    private sealed class AsyncChangingRules(IRuleRepository inner, Func<Task> change) : IRuleRepository
    {
        public async Task<List<TriggerRule>> GetAllAsync(CancellationToken ct) { var rules = await inner.GetAllAsync(ct); await change(); return rules; }
        public Task<TriggerRule?> GetByIdAsync(int id, CancellationToken ct) => inner.GetByIdAsync(id, ct);
        public Task<TriggerRule> CreateAsync(TriggerRule rule, CancellationToken ct) => inner.CreateAsync(rule, ct);
        public Task UpdateAsync(TriggerRule rule, CancellationToken ct) => inner.UpdateAsync(rule, ct);
        public Task RecordEvaluationAsync(int ruleId, DateTime when, CancellationToken ct) => inner.RecordEvaluationAsync(ruleId, when, ct);
        public Task DeleteAsync(int id, string configurationVersion, CancellationToken ct) => inner.DeleteAsync(id, configurationVersion, ct);
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        private event Action<T, string?>? Changed;
        public T CurrentValue { get; private set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) { Changed += listener; return new Subscription(() => Changed -= listener); }
        public void Change(T value) { CurrentValue = value; Changed?.Invoke(value, null); }
        private sealed class Subscription(Action remove) : IDisposable { public void Dispose() => remove(); }
    }
    private sealed class RejectingFactory : IDbContextFactory<DeyeSolarDbContext>
    { public DeyeSolarDbContext CreateDbContext() => throw new InvalidOperationException("Database must not be opened."); }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options, TestInstallation.Id);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }
    private sealed class Database(Factory factory) : IAsyncDisposable
    {
        public Factory Factory { get; } = factory;
        public ExportReadingStore Store { get; } = new(factory, new Clock());
        public static async Task<Database> CreateAsync()
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
            { InitialCatalog = "SolarRefreshTests_" + Guid.NewGuid().ToString("N") };
            var factory = new Factory(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options);
            await using var db = factory.CreateDbContext();
            await db.Database.MigrateAsync();
                await TestInstallation.EnsureAsync(db);
            return new(factory);
        }
        public async ValueTask DisposeAsync()
        {
            await using var db = Factory.CreateDbContext();
            await db.Database.EnsureDeletedAsync();
        }
    }
}

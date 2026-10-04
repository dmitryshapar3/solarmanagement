using System.Text.Json;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Tests;

public sealed class LegacyIntegrationBootstrapTests
{
    [SqlServerFact]
    public async Task LegacyPvConfirmationSurvivesRuntimeSeedingReloadAndReconfirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.Factory("site-a").CreateDbContext())
        {
            // This is the persisted pre-upgrade format, without the new generic aliases.
            db.AppSettings.Add(new() { Section = "SolarEstimate", Key = "DeyeSolarPowerIsPvDcConfirmed", Value = "True" });
            await db.SaveChangesAsync();
            Assert.False(await db.AppSettings.AnyAsync(setting => setting.Key == "ConfirmedInverterKey" || setting.Key == "SolarPowerIsPvDcConfirmed"));
        }
        var neighbor = await fixture.StateAsync("site-b");
        Assert.True(await fixture.Bootstrap.RunAsync(fixture.Factory("site-a"), fixture.Effective, default));
        string selected;
        await using (var db = fixture.Factory("site-a").CreateDbContext())
            selected = (await db.IntegrationDeviceBindings.SingleAsync(binding => binding.Kind == "inverter")).Id.ToString("D");

        using var builder = fixture.RuntimeFactory();
        await using var runtime = await builder.CreateAsync("site-a");
        var monitor = runtime.Resolve<IOptionsMonitor<SolarEstimateOptions>>();
        Assert.True(monitor.CurrentValue.SolarPowerIsPvDcConfirmed);
        Assert.Equal(selected, monitor.CurrentValue.ConfirmedInverterKey);
        var settings = runtime.Resolve<AppSettingsService>();
        var loaded = await settings.LoadSectionAsync<SolarEstimateOptions>(SolarEstimateOptions.Section);
        Assert.True(loaded.SolarPowerIsPvDcConfirmed);
        Assert.Equal(selected, loaded.ConfirmedInverterKey);
        await using (var db = fixture.Factory("site-a").CreateDbContext())
            Assert.False(await db.AppSettings.AnyAsync(setting => setting.Key == "ConfirmedInverterKey" || setting.Key == "SolarPowerIsPvDcConfirmed"));

        await settings.SaveSectionAsync(SolarEstimateOptions.Section,
            new { DeyeSolarPowerIsPvDcConfirmed = false, DeyeConfirmedDeviceSn = "" });
        Assert.False(monitor.CurrentValue.SolarPowerIsPvDcConfirmed);
        Assert.Equal("", monitor.CurrentValue.ConfirmedInverterKey);
        await settings.SaveSectionAsync(SolarEstimateOptions.Section,
            new { DeyeSolarPowerIsPvDcConfirmed = true, DeyeConfirmedDeviceSn = selected });
        await runtime.RefreshSettingsAsync();
        Assert.True(monitor.CurrentValue.SolarPowerIsPvDcConfirmed);
        Assert.Equal(selected, monitor.CurrentValue.ConfirmedInverterKey);
        await using var restarted = await builder.CreateAsync("site-a");
        var afterRestart = restarted.Resolve<IOptionsMonitor<SolarEstimateOptions>>().CurrentValue;
        Assert.True(afterRestart.SolarPowerIsPvDcConfirmed);
        Assert.Equal(selected, afterRestart.ConfirmedInverterKey);
        Assert.Equal(neighbor, await fixture.StateAsync("site-b"));
    }

    [SqlServerFact]
    public async Task StaleGenericConfirmationRowsCannotAuthorizeAnotherInverter()
    {
        await using var fixture = await Fixture.CreateAsync();
        var neighbor = await fixture.StateAsync("site-b");
        Assert.True(await fixture.Bootstrap.RunAsync(fixture.Factory("site-a"), fixture.Effective, default));
        string selected;
        await using (var db = fixture.Factory("site-a").CreateDbContext())
        {
            selected = (await db.IntegrationDeviceBindings.SingleAsync(binding => binding.Kind == "inverter")).Id.ToString("D");
            db.AppSettings.AddRange(
                new AppSetting { Section = "SolarEstimate", Key = "DeyeSolarPowerIsPvDcConfirmed", Value = "False" },
                new AppSetting { Section = "SolarEstimate", Key = "SolarPowerIsPvDcConfirmed", Value = "True" },
                new AppSetting { Section = "SolarEstimate", Key = "ConfirmedInverterKey", Value = Guid.NewGuid().ToString("D") });
            await db.SaveChangesAsync();
        }
        using var builder = fixture.RuntimeFactory();
        await using var runtime = await builder.CreateAsync("site-a");
        var configured = runtime.Resolve<IOptionsMonitor<SolarEstimateOptions>>().CurrentValue;
        var loaded = await runtime.Resolve<AppSettingsService>().LoadSectionAsync<SolarEstimateOptions>(SolarEstimateOptions.Section);
        foreach (var options in new[] { configured, loaded })
        {
            Assert.False(options.SolarPowerIsPvDcConfirmed);
            Assert.Equal(selected, options.ConfirmedInverterKey);
        }
        Assert.Equal(neighbor, await fixture.StateAsync("site-b"));
    }

    [SqlServerFact]
    public async Task UpgradeImportsEffectiveCredentialsAndOnlyProvenSelectedHistoryAndPreservesNeighbor()
    {
        await using var fixture = await Fixture.CreateAsync();
        var neighbor = await fixture.StateAsync("site-b");
        var changes = new List<(string Installation, Guid Instance)>();
        fixture.Notifier.Changed += (site, instance) => changes.Add((site, instance));
        Assert.True(await fixture.Bootstrap.RunAsync(fixture.Factory("site-a"), fixture.Effective, default));
        await using var db = fixture.Factory("site-a").CreateDbContext();
        var inverter = await db.IntegrationDeviceBindings.SingleAsync(binding => binding.Kind == "inverter");
        var socket = await db.IntegrationDeviceBindings.SingleAsync(binding => binding.Kind == "socket");
        Assert.Equal("SelectedCase", inverter.RemoteId);
        Assert.True(inverter.IsDefault);
        Assert.Equal("SocketCase", socket.RemoteId);
        Assert.Equal("0", socket.Channel);
        Assert.Equal(2, await db.IntegrationInstances.CountAsync());
        var deye = await db.IntegrationInstances.SingleAsync(instance => instance.ProviderId == "deye.cloud");
        var config = await db.IntegrationConfigurations.SingleAsync(value => value.InstanceId == deye.Id);
        Assert.Equal("persisted-app", JsonDocument.Parse(config.ValuesJson).RootElement.GetProperty("appId").GetString());
        Assert.DoesNotContain("fixture-secret", config.SecretsCiphertext);
        Assert.Equal("persisted-fixture-secret", fixture.Secrets.Decrypt("site-a", deye.Id, 1, config.SecretsCiphertext)["appSecret"]);
        var readings = await db.Readings.OrderBy(value => value.Timestamp).ToArrayAsync();
        Assert.Equal(new[] { inverter.Id.ToString("D"), "SelectedCase", "selectedcase", "other-source" }, readings.Select(value => value.SolarDeviceSn));
        Assert.Equal(new[] { 0, 500, 800, 900 }, readings.Select(value => value.SolarProduction));
        Assert.Equal(new[] { -1200, 1200, -1500, 1600 }, readings.Select(value => value.BatteryPower));
        Assert.All(readings, value => { Assert.Null(value.InverterId); Assert.Null(value.BatterySocValid); });
        var exports = await db.ExportReadings.OrderBy(value => value.ObservedAt).ToArrayAsync();
        Assert.Equal(new[] { inverter.Id.ToString("D"), "selectedcase", "other-source" }, exports.Select(value => value.DeviceSn));
        Assert.Equal(new[] { -1100, -2200, 3300 }, exports.Select(value => value.GridPowerWatts));
        var rule = await db.TriggerRules.SingleAsync();
        Assert.Equal(socket.Id.ToString("D"), rule.EntityId);
        Assert.Null(rule.SourceInverterId);
        Assert.True(rule.CurrentState);
        Assert.True(rule.Enabled);
        var labels = JsonSerializer.Deserialize<Dictionary<string, string>>((await db.AppSettings.SingleAsync(value => value.Section == "DeviceLabels")).Value)!;
        Assert.Equal("Saved socket label", labels[Fixture.OldLabelKey]);
        Assert.Equal("Saved socket label", labels[DeviceNameService.LabelKey(socket.Id.ToString("D"))]);
        Assert.Equal(2, labels.Count);
        Assert.False(await db.AppSettings.AnyAsync(value => value.Section == "DeyeCloud" && (value.Key == "AppSecret" || value.Key == "Password")
            || value.Section == "Shelly" && value.Key == "AuthKey"));
        Assert.Equal("1", (await db.AppSettings.SingleAsync(value => value.Section == "IntegrationMigration")).Value);
        Assert.Equal(inverter.Id.ToString("D"), (await db.AppSettings.SingleAsync(value => value.Key == "DeyeConfirmedDeviceSn")).Value);
        Assert.Equal(neighbor, await fixture.StateAsync("site-b"));
        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Equal("site-a", change.Installation));
        var imported = await fixture.StateAsync("site-a");
        Assert.True(await fixture.Bootstrap.RunAsync(fixture.Factory("site-a"), fixture.Effective, default));
        Assert.Equal(imported, await fixture.StateAsync("site-a"));
        Assert.Equal(2, changes.Count);
    }

    [SqlServerFact]
    public async Task MissingRequiredPackageLeavesBothProvidersSecretsRulesAndHistoryUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Catalog.IncludeShelly = false;
        var first = await fixture.StateAsync("site-a");
        var neighbor = await fixture.StateAsync("site-b");
        Assert.False(await fixture.Bootstrap.RunAsync(fixture.Factory("site-a"), fixture.Effective, default));
        Assert.Equal(first, await fixture.StateAsync("site-a"));
        Assert.Equal(neighbor, await fixture.StateAsync("site-b"));
        fixture.Catalog.IncludeShelly = true;
        Assert.True(await fixture.Bootstrap.RunAsync(fixture.Factory("site-a"), fixture.Effective, default));
    }

    [SqlServerFact]
    public async Task FailureAtCompletionRollsBackEncryptedConfigurationHistoryAliasesAndDeletedSecrets()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.StateAsync("site-a");
        var neighbor = await fixture.StateAsync("site-b");
        var changed = false;
        fixture.Notifier.Changed += (_, _) => changed = true;
        await using (var db = fixture.Factory("site-a").CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE AppSettings ADD CONSTRAINT RejectIntegrationCompletion CHECK (Section <> 'IntegrationMigration' OR Value <> '1')");
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Bootstrap.RunAsync(fixture.Factory("site-a"), fixture.Effective, default));
        Assert.Equal(first, await fixture.StateAsync("site-a"));
        Assert.Equal(neighbor, await fixture.StateAsync("site-b"));
        Assert.False(changed);
        await using (var db = fixture.Factory("site-a").CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE AppSettings DROP CONSTRAINT RejectIntegrationCompletion");
        Assert.True(await fixture.Bootstrap.RunAsync(fixture.Factory("site-a"), fixture.Effective, default));
    }
    [SqlServerFact]
    public async Task LegacyIntervalIsPreservedOrEntireCutoverIsRefusedWithoutChanges()
    {
        foreach (var (interval, compatible) in new[] { (60000, true), (120000, true), (120001, false), (int.MaxValue, false) })
        {
            await using var fixture = await Fixture.CreateAsync();
            await using (var db = fixture.Factory("site-a").CreateDbContext())
            {
                db.AppSettings.Add(new() { Section = "Shelly", Key = "RequestIntervalMilliseconds", Value = interval.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                await db.SaveChangesAsync();
            }
            var first = await fixture.StateAsync("site-a");
            var neighbor = await fixture.StateAsync("site-b");
            var changed = false;
            fixture.Notifier.Changed += (_, _) => changed = true;
            Assert.Equal(compatible, await fixture.Bootstrap.RunAsync(fixture.Factory("site-a"), fixture.Effective, default));
            Assert.Equal(neighbor, await fixture.StateAsync("site-b"));
            if (!compatible)
            {
                Assert.Equal(first, await fixture.StateAsync("site-a"));
                Assert.False(changed);
            }
            else
            {
                await using var db = fixture.Factory("site-a").CreateDbContext();
                var instance = await db.IntegrationInstances.SingleAsync(item => item.ProviderId == "shelly.cloud");
                var config = await db.IntegrationConfigurations.SingleAsync(item => item.InstanceId == instance.Id);
                Assert.Equal(interval, JsonDocument.Parse(config.ValuesJson).RootElement.GetProperty("requestIntervalMilliseconds").GetInt32());
                Assert.Equal("1", (await db.AppSettings.SingleAsync(item => item.Section == "IntegrationMigration")).Value);
            }
        }
    }

    [SqlServerFact]
    public async Task ConcurrentStartupAndInstallCutoversCreateOneImportAndBothReturnCompleted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var neighbor = await fixture.StateAsync("site-b");
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => fixture.Bootstrap.RunAsync(fixture.Factory("site-a"), fixture.Effective, default)));
        Assert.All(results, Assert.True);
        await using var db = fixture.Factory("site-a").CreateDbContext();
        Assert.Equal(2, await db.IntegrationInstances.CountAsync());
        Assert.Equal(2, await db.IntegrationConfigurations.CountAsync());
        Assert.Equal(2, await db.IntegrationDeviceBindings.CountAsync());
        Assert.Equal(2, await db.IntegrationDeviceAliases.CountAsync());
        Assert.Single(await db.AppSettings.Where(value => value.Section == "IntegrationMigration").ToArrayAsync());
        Assert.Equal(neighbor, await fixture.StateAsync("site-b"));
    }

    private sealed class Fixture(DbContextOptions<DeyeSolarDbContext> options) : IAsyncDisposable
    {
        public const string OldLabelKey = "5907A83AB52D3933E1A5A0EF698036473B7792384339435C85C823225A953849";
        public Catalog Catalog { get; } = new();
        public IntegrationSecretStore Secrets { get; } = new(new EphemeralDataProtectionProvider());
        public IntegrationChangeNotifier Notifier { get; } = new(NullLogger<IntegrationChangeNotifier>.Instance);
        public IConfiguration Effective { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DeyeCloud:AppId"] = "deployment-app",
            ["DeyeCloud:AppSecret"] = "deployment-fixture-secret",
            ["DeyeCloud:Email"] = "fixture@example.test",
            ["DeyeCloud:Password"] = "deployment-fixture-password",
            ["DeyeCloud:DeviceSn"] = "SelectedCase",
            ["Shelly:ServerUri"] = "https://shelly-1-eu.shelly.cloud",
            ["Shelly:AuthKey"] = "deployment-shelly-fixture-secret",
            ["Shelly:DeviceId"] = "SocketCase"
        }).Build();
        public LegacyIntegrationBootstrap Bootstrap => new(Catalog, Secrets, TimeProvider.System, Notifier);
        public IDbContextFactory<DeyeSolarDbContext> Factory(string site) => new Factory(options, site);
        public TenantRuntimeFactory RuntimeFactory() => new(options, Effective, NullLoggerFactory.Instance, TimeProvider.System,
            new Lifetime(), new TenantTestExecutor(), Secrets, Notifier, integrationBootstrap: Bootstrap);
        public async Task<string> StateAsync(string site)
        {
            await using var db = Factory(site).CreateDbContext();
            return JsonSerializer.Serialize(new
            {
                settings = await db.AppSettings.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
                readings = await db.Readings.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
                exports = await db.ExportReadings.AsNoTracking().OrderBy(value => value.ObservedAt).ToArrayAsync(),
                rules = await db.TriggerRules.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
                instances = await db.IntegrationInstances.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
                configs = await db.IntegrationConfigurations.AsNoTracking().OrderBy(value => value.InstanceId).ThenBy(value => value.Revision).ToArrayAsync(),
                bindings = await db.IntegrationDeviceBindings.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
                aliases = await db.IntegrationDeviceAliases.AsNoTracking().OrderBy(value => value.LegacyId).ToArrayAsync()
            });
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION"))
            { InitialCatalog = "LegacyIntegrationUpgrade_" + Guid.NewGuid().ToString("N") };
            var fixture = new Fixture(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options);
            await using var db = fixture.Factory("site-a").CreateDbContext();
            try
            {
                var migrator = db.GetService<IMigrator>();
                await migrator.MigrateAsync("20261001202236_AddInstallations");
                var created = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Installations (Id, Name, CreatedAt, IsEnabled) VALUES ({"site-a"}, {"First"}, {created}, 1), ({"site-b"}, {"Neighbor"}, {created}, 1)");
                var observed = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
                foreach (var site in new[] { "site-a", "site-b" })
                {
                    var labels = JsonSerializer.Serialize(new Dictionary<string, string> { [OldLabelKey] = "Saved socket label" });
                    var values = new Dictionary<(string Section, string Key), string>
                    {
                        [("DeyeCloud", "AppId")] = "persisted-app",
                        [("DeyeCloud", "AppSecret")] = "persisted-fixture-secret",
                        [("DeyeCloud", "Password")] = "persisted-fixture-password",
                        [("Shelly", "AuthKey")] = "persisted-shelly-fixture-secret",
                        [("SolarEstimate", "DeyeConfirmedDeviceSn")] = "SelectedCase",
                        [("DeviceLabels", "LabelsJson")] = labels,
                        [("IntegrationMigration", "Completed")] = "0"
                    };
                    foreach (var ((section, key), value) in values)
                        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AppSettings (InstallationId, Section, [Key], Value) VALUES ({site}, {section}, {key}, {value})");
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO TriggerRules (InstallationId, Name, EntityId, Enabled, SocTurnOnThreshold, SocTurnOffThreshold,
                          UseSeparateSocTurnOffThreshold, UseSolarProductionThreshold, MinAverageSolarProductionWatts, CooldownMinutes,
                          IntervalSeconds, CurrentState) VALUES ({site}, {"Legacy socket rule"}, {"shelly:SocketCase"}, 1, 80, 70, 1, 0, 3000, 15, 30, 1)
                        """);
                    var identifiers = new[] { "SelectedCase", "SelectedCase", "selectedcase", "other-source" };
                    var pv = new[] { 0, 500, 800, 900 };
                    var battery = new[] { -1200, 1200, -1500, 1600 };
                    for (var index = 0; index < identifiers.Length; index++)
                    {
                        var time = observed.AddMinutes(index);
                        DateTime? measured = index == 1 ? null : time;
                        await db.Database.ExecuteSqlInterpolatedAsync($"""
                            INSERT INTO Readings (InstallationId, Timestamp, BatterySoc, BatteryTemperature, BatteryVoltage, BatteryPower,
                              BatteryCurrent, SolarProduction, SolarObservedAt, SolarDeviceSn, GridConsumption, LoadPower, DataSource)
                            VALUES ({site}, {time}, 95, 25, 48, {battery[index]}, 10, {pv[index]}, {measured}, {identifiers[index]}, -1100, 600, {"DeyeCloud"})
                            """);
                    }
                    var exportIds = new[] { "SelectedCase", "selectedcase", "other-source" };
                    var grid = new[] { -1100, -2200, 3300 };
                    for (var index = 0; index < exportIds.Length; index++)
                    {
                        var time = observed.AddMinutes(index);
                        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ExportReadings (InstallationId, DeviceSn, ObservedAt, GridPowerWatts, PolledAt) VALUES ({site}, {exportIds[index]}, {time}, {grid[index]}, {time})");
                    }
                }
                await migrator.MigrateAsync();
                return fixture;
            }
            catch { await db.Database.EnsureDeletedAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await using var db = Factory("site-a").CreateDbContext(); await db.Database.EnsureDeletedAsync(); }
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options, string site) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options, site);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
    private sealed class Catalog : IIntegrationProviderCatalog
    {
        public bool IncludeShelly { get; set; } = true;
        public Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IntegrationProviderDescriptor>>(IncludeShelly ? [Descriptor("deye.cloud"), Descriptor("shelly.cloud")] : [Descriptor("deye.cloud")]);
        public async Task<IntegrationProviderDescriptor> GetAsync(string providerId, string? version, CancellationToken ct)
            => (await GetProvidersAsync(ct)).Single(value => value.ProviderId == providerId);
        private static IntegrationProviderDescriptor Descriptor(string provider) => new(provider, "1.0.0", new string('A', 64), new string('B', 64), provider, 1, 1, [], [], ["test", "discover"]);
    }
}

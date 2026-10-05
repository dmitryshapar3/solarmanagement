using System.Collections.Concurrent;
using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;

namespace DeyeSolar.Web.Tests;

public class BillingRuntimeTests
{
    private const string ExpiredSite = "billing-runtime-expired";
    private const string ActiveSite = "billing-runtime-active";
    private const string ExpiredOwner = "billing-runtime-expired-owner";
    private const string ActiveOwner = "billing-runtime-active-owner";

    [SqlServerFact]
    public async Task ExpiredDueCycleHasNoProviderOrPersistenceEffectsWhileIndependentActiveAutomationWorks()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetTrialStartAsync(ExpiredOwner, fixture.Clock.Now.AddMonths(-1));
        using var factory = fixture.RuntimeFactory();
        await using var expired = await factory.CreateAsync(ExpiredSite);
        await using var active = await factory.CreateAsync(ActiveSite);
        expired.Resolve<DeviceStatusSnapshot>().Update([new("cached-expired", "Old cached socket", "Socket", true, true, 25)]);
        var expiredBefore = await fixture.StateAsync(ExpiredSite);
        var activeBefore = await fixture.StateAsync(ActiveSite);

        await expired.RunDueWorkAsync(default);

        Assert.Empty(fixture.Executor.Calls);
        Assert.Null(expired.Resolve<DeviceStatusSnapshot>().Current);
        Assert.Equal(expiredBefore, await fixture.StateAsync(ExpiredSite));
        Assert.Equal(activeBefore, await fixture.StateAsync(ActiveSite));
        await active.RunDueWorkAsync(default);
        Assert.Contains(fixture.Executor.Calls, call => call == (ActiveSite, "inverter.read"));
        Assert.Contains(fixture.Executor.Calls, call => call == (ActiveSite, "socket.set"));
        Assert.DoesNotContain(fixture.Executor.Calls, call => call.Installation == ExpiredSite);
        await using (var db = fixture.Db(ActiveSite))
        {
            Assert.True((await db.TriggerRules.SingleAsync()).CurrentState);
            Assert.Equal("acknowledged", (await db.IntegrationCommands.SingleAsync()).Status);
            Assert.Equal(2, await db.Readings.CountAsync());
        }
        Assert.Equal(expiredBefore, await fixture.StateAsync(ExpiredSite));
    }

    [SqlServerFact]
    public async Task ExpiryAfterAcceptedInverterReadPreventsAutomatedSocketMutationAndKeepsNeighbourUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetTrialStartAsync(ExpiredOwner, fixture.Clock.Now.AddMonths(-1).AddSeconds(1));
        using var factory = fixture.RuntimeFactory();
        await using var runtime = await factory.CreateAsync(ExpiredSite);
        var neighbourBefore = await fixture.StateAsync(ActiveSite);
        fixture.Executor.AfterInverterRead = installation =>
        {
            if (installation == ExpiredSite) fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        };

        await runtime.RunDueWorkAsync(default);

        Assert.Equal(new[] { (ExpiredSite, "inverter.read") }, fixture.Executor.Calls.ToArray());
        Assert.False((await fixture.Access.ReadAsync(ExpiredOwner)).HasAccess);
        await using (var db = fixture.Db(ExpiredSite))
        {
            Assert.Empty(await db.IntegrationCommands.ToListAsync());
            var rule = await db.TriggerRules.SingleAsync();
            Assert.False(rule.CurrentState);
            Assert.NotNull(rule.LastEvaluated);
            // The accepted inverter read and a denied rule evaluation still finish their local bookkeeping.
            Assert.Single(await db.Readings.ToListAsync());
            Assert.Equal("ERROR", (await db.RuleRunLogs.SingleAsync()).Action);
        }
        Assert.Equal(neighbourBefore, await fixture.StateAsync(ActiveSite));
    }

    [SqlServerFact]
    public async Task MissingCoOwnersBillingRecordCannotDisableSharedAutomationForVerifiedPayingMember()
    {
        await using var fixture = await Fixture.CreateAsync(appleEnabled: true);
        await using (var db = fixture.Db())
        {
            db.BillingAccounts.Remove(await db.BillingAccounts.SingleAsync(a => a.UserId == ExpiredOwner));
            db.InstallationMemberships.Add(new() { InstallationId = ExpiredSite, UserId = ActiveOwner, Role = "Owner" });
            var payer = await db.BillingAccounts.SingleAsync(a => a.UserId == ActiveOwner);
            db.AppleSubscriptions.Add(new()
            {
                UserId = ActiveOwner,
                AppAccountToken = payer.AppAccountToken,
                OriginalTransactionId = "runtime-paid-original",
                TransactionId = "runtime-paid-transaction",
                ProductId = "com.dshapar.solar.monthly",
                Environment = "Production",
                Status = AppleSubscriptionStatus.Active,
                ExpiresAt = fixture.Clock.Now.AddMonths(1),
                CheckedAt = fixture.Clock.Now,
                SourceSignedAt = fixture.Clock.Now,
                ObservationStartedAt = fixture.Clock.Now
            });
            await db.SaveChangesAsync();
        }
        Assert.True(await fixture.Access.InstallationHasAccessAsync(ExpiredSite, default));
        await Assert.ThrowsAsync<BillingAccessException>(() => fixture.Access.ReadAsync(ExpiredOwner));
        using var factory = fixture.RuntimeFactory();
        await using var runtime = await factory.CreateAsync(ExpiredSite);
        var neighbourBefore = await fixture.StateAsync(ActiveSite);
        await runtime.RunDueWorkAsync(default);
        Assert.Contains(fixture.Executor.Calls, call => call == (ExpiredSite, "socket.set"));
        Assert.Equal(neighbourBefore, await fixture.StateAsync(ActiveSite));
    }

    private sealed class Fixture(DbContextOptions<DeyeSolarDbContext> options, Clock clock, Executor executor,
        IntegrationSecretStore secrets, BillingAccessService access, SqlServerTestDatabase database) : IAsyncDisposable
    {
        public Clock Clock { get; } = clock;
        public Executor Executor { get; } = executor;
        public BillingAccessService Access { get; } = access;
        public DeyeSolarDbContext Db(string? installation = null) => installation is null ? new(options) : new(options, installation);
        public TenantRuntimeFactory RuntimeFactory() => new(options, NullLoggerFactory.Instance,
            Clock, new Lifetime(), Executor, secrets, new IntegrationChangeNotifier(NullLogger<IntegrationChangeNotifier>.Instance), billing: Access);
        public async Task SetTrialStartAsync(string userId, DateTimeOffset start)
        {
            await using var db = Db();
            (await db.BillingAccounts.SingleAsync(a => a.UserId == userId)).TrialStartedAt = start;
            await db.SaveChangesAsync();
        }
        public async Task<string> StateAsync(string installation)
        {
            await using var db = Db(installation);
            return JsonSerializer.Serialize(new
            {
                Devices = await db.IntegrationDeviceBindings.AsNoTracking().OrderBy(d => d.Id).ToArrayAsync(),
                Settings = await db.AppSettings.AsNoTracking().OrderBy(s => s.Id).ToArrayAsync(),
                Readings = await db.Readings.AsNoTracking().OrderBy(r => r.Id).ToArrayAsync(),
                Rules = await db.TriggerRules.AsNoTracking().OrderBy(r => r.Id).ToArrayAsync(),
                Logs = await db.RuleRunLogs.AsNoTracking().OrderBy(r => r.Id).ToArrayAsync(),
                Commands = await db.IntegrationCommands.AsNoTracking().OrderBy(c => c.Id).ToArrayAsync()
            });
        }
        public static async Task<Fixture> CreateAsync(bool appleEnabled = false)
        {
            var database = await SqlServerTestDatabase.CreateAsync("SolarBillingRuntime");
            var options = database.Options;
            var clock = new Clock();
            var executor = new Executor(clock);
            var secrets = new IntegrationSecretStore(new EphemeralDataProtectionProvider());
            var fixture = new Fixture(options, clock, executor, secrets, new(options, clock, new AppleBillingOptions { Enabled = appleEnabled }), database);
            try
            {
                await using (var db = fixture.Db())
                {
                    db.Installations.AddRange(new Installation { Id = ExpiredSite, Name = "Expired runtime", CreatedAt = clock.Now },
                        new Installation { Id = ActiveSite, Name = "Independent runtime", CreatedAt = clock.Now });
                    db.Users.AddRange(new IdentityUser { Id = ExpiredOwner, UserName = "expired-runtime" },
                        new IdentityUser { Id = ActiveOwner, UserName = "active-runtime" });
                    db.InstallationMemberships.AddRange(new InstallationMembership { InstallationId = ExpiredSite, UserId = ExpiredOwner, Role = "Owner" },
                        new InstallationMembership { InstallationId = ActiveSite, UserId = ActiveOwner, Role = "Owner" });
                    await db.SaveChangesAsync();
                }
                clock.Now = DateTimeOffset.UtcNow;
                // Keep precise boundary fixtures representable by a calendar-month trial on every CI run date.
                if (clock.Now.Day > 28) clock.Now = clock.Now.AddDays(32 - clock.Now.Day);
                foreach (var id in new[] { ExpiredSite, ActiveSite })
                {
                    await using var db = fixture.Db(id);
                    var instance = Guid.NewGuid(); var inverter = Guid.NewGuid(); var socket = Guid.NewGuid();
                    db.IntegrationInstances.Add(new()
                    {
                        Id = instance,
                        ProviderId = "runtime.fixture",
                        Name = "Runtime billing fixture",
                        PackageVersion = "1.0.0",
                        PackageDigest = "runtime-fixture-digest",
                        DescriptorDigest = "runtime-fixture-ui",
                        ConfigurationVersion = 1,
                        State = "enabled",
                        CreatedAt = clock.Now,
                        UpdatedAt = clock.Now
                    });
                    db.IntegrationConfigurations.Add(new()
                    {
                        InstanceId = instance,
                        Revision = 1,
                        ValuesJson = "{}",
                        SecretsCiphertext = secrets.Encrypt(id, instance, 1, new Dictionary<string, string>()),
                        CreatedAt = clock.Now
                    });
                    db.IntegrationDeviceBindings.AddRange(new IntegrationDeviceBindingEntity
                    {
                        Id = inverter,
                        InstanceId = instance,
                        Kind = "inverter",
                        RemoteId = "fixture-inverter",
                        Name = "Fixture inverter",
                        IsDefault = true,
                        MetadataJson = "{\"capabilities\":{\"hasBattery\":true,\"hasSolarPower\":true,\"hasSignedGridPower\":true,\"hasLoadPower\":true,\"solarBasis\":\"PvDc\"}}"
                    }, new IntegrationDeviceBindingEntity
                    {
                        Id = socket,
                        InstanceId = instance,
                        Kind = "socket",
                        RemoteId = "fixture-socket",
                        Channel = "0",
                        Name = "Fixture socket",
                        MetadataJson = "{\"capabilities\":{\"canSwitch\":true}}"
                    });
                    db.TriggerRules.Add(new TriggerRule
                    { Name = "Charge surplus automation", EntityId = socket.ToString("D"), Enabled = true, SocTurnOnThreshold = 50 });
                    // The expired cycle must not even delete old retained data; the active cycle may clean its own history.
                    db.Readings.Add(new Reading { Timestamp = DateTime.UtcNow.AddDays(id == ExpiredSite ? -40 : 0), SolarProduction = 123 });
                    await db.SaveChangesAsync();
                }
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
    private sealed class Executor(Clock clock) : IIntegrationRuntimeExecutor
    {
        public ConcurrentQueue<(string Installation, string Method)> Calls { get; } = new();
        public Action<string>? AfterInverterRead { get; set; }
        public Task<JsonElement> InvokeAsync(IntegrationSession session, string method, JsonElement parameters, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Enqueue((session.InstallationId, method));
            var now = clock.Now;
            ProviderMeasurement Measurement(decimal value) => new(value, now, ProviderMeasurementQuality.Good);
            var result = method switch
            {
                "inverter.read" => IntegrationJson.Element(new ProviderInverterTelemetry("fixture-inverter", now, "PvDc", Measurement(99),
                    Measurement(200), Measurement(22), Measurement(52), Measurement(4), Measurement(2500), Measurement(-100), Measurement(2200))),
                "socket.read" => IntegrationJson.Element(new ProviderSocketTelemetry("fixture-socket", "0", false, true, 0, now, now)),
                "socket.set" => IntegrationJson.Element(new ProviderSocketCommandResult(parameters.GetProperty("commandId").GetString()!, "acknowledged")),
                _ => throw new InvalidOperationException("Unexpected provider method in the runtime billing fixture.")
            };
            if (method == "inverter.read") AfterInverterRead?.Invoke(session.InstallationId);
            return Task.FromResult(result);
        }
        public Task StopAsync(Guid instanceId, CancellationToken ct) => Task.CompletedTask;
    }
}

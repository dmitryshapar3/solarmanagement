using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Localization;
using DeyeSolar.Web.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Tests;

public class BillingSqlServerTests
{
    private const string Password = "Local billing password 42!";
    private const string SiteA = "billing-site-a";
    private const string SiteB = "billing-site-b";
    private const string OwnerA = "billing-owner-a";
    private const string OwnerB = "billing-owner-b";

    [SqlServerFact]
    public async Task CalendarMonthTrialEndsAtExactUtcBoundaryAndNeverRestartsOnRead()
    {
        await using var host = await Host.StartAsync();
        var start = new DateTimeOffset(2027, 1, 31, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2027, 2, 28, 23, 30, 0, TimeSpan.Zero), BillingAccount.Create("calendar-fixture", start).TrialEndsAt);
        await host.SetTrialAsync(OwnerA, start, new DateTimeOffset(2027, 2, 28, 23, 30, 0, TimeSpan.Zero));
        var before = await host.BillingStateAsync();
        var access = host.Access();
        host.Clock.Now = start.AddTicks(-1);
        Assert.False((await access.ReadAsync(OwnerA)).HasAccess);
        host.Clock.Now = start;
        Assert.True((await access.ReadAsync(OwnerA)).HasAccess);
        host.Clock.Now = new DateTimeOffset(2027, 2, 28, 23, 29, 59, TimeSpan.Zero);
        Assert.Equal("trial", (await access.ReadAsync(OwnerA)).Status);
        host.Clock.Now = new DateTimeOffset(2027, 2, 28, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal("expired", (await access.ReadAsync(OwnerA)).Status);
        host.Clock.Now = host.Clock.Now.AddYears(1);
        Assert.False((await access.ReadAsync(OwnerA)).HasAccess);
        Assert.Equal(before, await host.BillingStateAsync());
    }

    [SqlServerFact]
    public async Task ExpiredBearerAndCookieCannotReadOrMutateSocketsWhileIdentityRemainsAvailable()
    {
        await using var host = await Host.StartAsync();
        var first = await host.LoginAsync(OwnerA);
        var second = await host.LoginAsync(OwnerB);
        using var browser = await host.CookieClientAsync(OwnerA);
        var own = await host.CreateSavedAsync(SiteA, "Own connection");
        var neighbour = await host.CreateSavedAsync(SiteB, "Independent connection");
        var ownDevice = await host.AddSocketAsync(SiteA, own.Id);
        await host.AddSocketAsync(SiteB, neighbour.Id);
        using var independent = host.BearerClient(second);
        using (var initialized = await independent.GetAsync($"/api/v2/integrations/{neighbour.Id}/devices"))
            Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
        await host.SetTrialAsync(OwnerA, host.Clock.Now.AddMonths(-1), host.Clock.Now);
        var before = await host.PersistedStateAsync();
        var calls = host.Executor.Calls;

        using var bearer = host.BearerClient(first);
        using (var preference = await bearer.PutAsJsonAsync("/api/account/language", new UserLanguageRequest("pl")))
            Assert.Equal(HttpStatusCode.OK, preference.StatusCode);
        using (var preference = await bearer.GetAsync("/api/account/language"))
            Assert.Equal("pl", (await preference.Content.ReadFromJsonAsync<UserLanguageRequest>())!.Language);
        using (var cookiePreference = await browser.GetAsync("/api/account/language"))
            Assert.Equal(HttpStatusCode.Unauthorized, cookiePreference.StatusCode);
        using (var forgedPreference = await bearer.GetAsync("/api/account/language/devices"))
            Assert.Equal(HttpStatusCode.NotFound, forgedPreference.StatusCode);
        await using (var claims = host.Factory().CreateDbContext())
        {
            Assert.Equal("pl", (await claims.UserClaims.AsNoTracking().SingleAsync(c => c.UserId == OwnerA
                && c.ClaimType == UserLanguageService.ClaimType)).ClaimValue);
            Assert.False(await claims.UserClaims.AnyAsync(c => c.UserId == OwnerB && c.ClaimType == UserLanguageService.ClaimType));
        }
        foreach (var client in new[] { bearer, browser })
        {
            using (var devices = await client.GetAsync($"/api/v2/integrations/{own.Id}/devices"))
                Assert.Equal(HttpStatusCode.PaymentRequired, devices.StatusCode);
            using (var telemetry = await client.GetAsync("/api/devices?refresh=true"))
                Assert.Equal(HttpStatusCode.PaymentRequired, telemetry.StatusCode);
            using (var command = await client.PostAsJsonAsync($"/api/v2/devices/{ownDevice}/commands",
                new IntegrationSocketCommandRequest(Guid.NewGuid(), true)))
                Assert.Equal(HttpStatusCode.PaymentRequired, command.StatusCode);
            using (var discovery = await client.PostAsJsonAsync($"/api/v2/integrations/{own.Id}/discovery", Change(own)))
                Assert.Equal(HttpStatusCode.PaymentRequired, discovery.StatusCode);
            using (var identity = await client.GetAsync("/api/auth/options"))
                Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
            using (var billing = await client.GetAsync("/api/billing/access"))
            {
                Assert.Equal(HttpStatusCode.OK, billing.StatusCode);
                Assert.False((await billing.Content.ReadFromJsonAsync<BillingAccess>())!.HasAccess);
            }
        }
        using (var allowed = await independent.GetAsync($"/api/v2/integrations/{neighbour.Id}/devices"))
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(calls, host.Executor.Calls);
        Assert.Equal(0, host.Executor.RuntimeCalls);
        Assert.Equal(0, host.DeviceReads);
        Assert.Equal(before, await host.PersistedStateAsync());
        using var stillCanSignIn = await host.Client.PostAsJsonAsync("/_fixture/login", new HttpLogin(OwnerA, Password, false));
        Assert.Equal(HttpStatusCode.OK, stillCanSignIn.StatusCode);
    }

    [SqlServerFact]
    public async Task ConcurrentTrialSelectionAcrossIntegrationsPersistsOneSocketAndReplayIsIdempotent()
    {
        await using var host = await Host.StartAsync();
        var first = await host.CreateSavedAsync(SiteA, "First provider");
        var second = await host.CreateSavedAsync(SiteA, "Second provider");
        var independent = await host.CreateSavedAsync(SiteB, "Independent provider");
        await host.AddSocketAsync(SiteB, independent.Id);
        var neighbourBefore = await host.InstallationStateAsync(SiteB);
        var drafts = new[] { (Instance: first, Draft: Change(first)), (Instance: second, Draft: Change(second)) };
        var selections = new List<(IntegrationInstanceDto Instance, SelectIntegrationDeviceRequest Request)>();
        foreach (var item in drafts)
        {
            var discovered = await host.Setup(SiteA).DiscoverAsync(item.Instance.Id, item.Draft, host.Actor(SiteA), default);
            selections.Add((item.Instance, new(item.Draft, Assert.Single(discovered.Devices).SelectionToken)));
        }
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = selections.Select(async item =>
        {
            await barrier.Task;
            try
            {
                var selected = await host.Setup(SiteA).SelectDeviceAsync(item.Instance.Id, item.Request, host.Actor(SiteA), default);
                return (Item: item, Binding: selected, Error: (IntegrationRequestException?)null);
            }
            catch (IntegrationRequestException error) { return (Item: item, Binding: (IntegrationBindingDto?)null, Error: error); }
        }).ToArray();
        barrier.SetResult();
        var outcomes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        var accepted = Assert.Single(outcomes, outcome => outcome.Binding is not null);
        var refused = Assert.Single(outcomes, outcome => outcome.Error is not null);
        Assert.Equal("trial_socket_limit", refused.Error!.Code);
        Assert.Equal(402, refused.Error.Status);
        var replay = await host.Setup(SiteA).SelectDeviceAsync(accepted.Item.Instance.Id, accepted.Item.Request, host.Actor(SiteA), default);
        Assert.Equal(accepted.Binding!.Id, replay.Id);
        await using var db = host.Factory(SiteA).CreateDbContext();
        Assert.Equal(accepted.Binding.Id, (await db.IntegrationDeviceBindings.SingleAsync()).Id);
        Assert.Equal(neighbourBefore, await host.InstallationStateAsync(SiteB));
    }

    [SqlServerFact]
    public async Task SelectionWaitingOnIntegrationLockCannotPersistAfterItsActorsTrialExpires()
    {
        await using var host = await Host.StartAsync();
        var own = await host.CreateSavedAsync(SiteA, "Queued trial selection");
        var neighbour = await host.CreateSavedAsync(SiteB, "Independent selected socket");
        await host.AddSocketAsync(SiteB, neighbour.Id);
        var deadline = host.Clock.Now;
        await host.SetTrialAsync(OwnerA, deadline.AddMonths(-1), deadline);
        // Moving the fixture clock back avoids inverting an unrepresentable end-of-month date.
        host.Clock.Now = deadline.AddSeconds(-2);
        var draft = Change(own);
        var discovery = await host.Setup(SiteA).DiscoverAsync(own.Id, draft, host.Actor(SiteA), default);
        var request = new SelectIntegrationDeviceRequest(draft, Assert.Single(discovery.Devices).SelectionToken);
        var before = await host.PersistedStateAsync();
        var providerCalls = host.Executor.Calls;
        var barrier = new BeforeInstanceLock();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var selection = host.Setup(SiteA, interceptor: barrier).SelectDeviceAsync(own.Id, request, host.Actor(SiteA), cancellation.Token);
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // Initial instance reads have completed; block only the atomic integration-lock acquisition.
            await using (var blocked = await InstanceRowLock.AcquireAsync(host.ConnectionString, own.Id))
            {
                barrier.Dispatch.TrySetResult();
                await blocked.WaitForBlockedSelectionAsync();
                Assert.False(selection.IsCompleted);
                Assert.True((await host.Access().ReadAsync(OwnerA)).HasAccess);
                host.Clock.Now = deadline;
            }
            await Assert.ThrowsAsync<BillingAccessException>(() => selection.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.False((await host.Access().ReadAsync(OwnerA)).HasAccess);
            Assert.Equal(before, await host.PersistedStateAsync());
            Assert.Equal(providerCalls, host.Executor.Calls);
            Assert.Equal(0, host.Executor.RuntimeCalls);
        }
        finally
        {
            barrier.Dispatch.TrySetResult();
            cancellation.Cancel();
            try { await selection; }
            catch (Exception exception) when (exception is BillingAccessException or OperationCanceledException) { }
        }
    }

    [SqlServerFact]
    public async Task OneOwnersTrialQuotaIncludesSocketsInAnotherOwnedInstallation()
    {
        await using var host = await Host.StartAsync();
        await using (var db = host.Factory().CreateDbContext())
        {
            db.InstallationMemberships.Add(new() { InstallationId = SiteB, UserId = OwnerA, Role = "Owner" });
            await db.SaveChangesAsync();
        }
        var own = await host.CreateSavedAsync(SiteA, "First installation");
        var other = await host.CreateSavedAsync(SiteB, "Second installation");
        await host.AddSocketAsync(SiteB, other.Id, OwnerA);
        var discovery = await host.Setup(SiteA).DiscoverAsync(own.Id, Change(own), host.Actor(SiteA), default);
        var before = await host.PersistedStateAsync();
        var denied = await Assert.ThrowsAsync<IntegrationRequestException>(() => host.Setup(SiteA).SelectDeviceAsync(own.Id,
            new(Change(own), Assert.Single(discovery.Devices).SelectionToken), host.Actor(SiteA), default));
        Assert.Equal("trial_socket_limit", denied.Code);
        Assert.Equal(before, await host.PersistedStateAsync());
    }

    [SqlServerFact]
    public async Task UnattributedBindingsDoNotConsumeAnAccountsTrialQuota()
    {
        await using var host = await Host.StartAsync();
        var unassigned = await host.CreateSavedAsync(SiteA, "Unattributed connection");
        var unassignedDevice = await host.AddSocketAsync(SiteA, unassigned.Id);
        await using (var db = host.Factory(SiteA).CreateDbContext())
        {
            (await db.IntegrationDeviceBindings.SingleAsync(binding => binding.Id == unassignedDevice)).AddedByUserId = null;
            await db.SaveChangesAsync();
        }
        var current = await host.CreateSavedAsync(SiteA, "Account's selected connection");
        var draft = Change(current);
        var discovered = await host.Setup(SiteA).DiscoverAsync(current.Id, draft, host.Actor(SiteA), default);
        var selected = await host.Setup(SiteA).SelectDeviceAsync(current.Id,
            new(draft, Assert.Single(discovered.Devices).SelectionToken), host.Actor(SiteA), default);
        await using (var db = host.Factory(SiteA).CreateDbContext())
        {
            Assert.Equal(2, await db.IntegrationDeviceBindings.CountAsync());
            Assert.Null((await db.IntegrationDeviceBindings.SingleAsync(binding => binding.Id == unassignedDevice)).AddedByUserId);
            Assert.Equal(OwnerA, (await db.IntegrationDeviceBindings.SingleAsync(binding => binding.Id == selected.Id)).AddedByUserId);
        }
        var another = await host.CreateSavedAsync(SiteA, "Second account selection");
        var anotherDraft = Change(another);
        var anotherDiscovery = await host.Setup(SiteA).DiscoverAsync(another.Id, anotherDraft, host.Actor(SiteA), default);
        var before = await host.PersistedStateAsync();
        var refused = await Assert.ThrowsAsync<IntegrationRequestException>(() => host.Setup(SiteA).SelectDeviceAsync(another.Id,
            new(anotherDraft, Assert.Single(anotherDiscovery.Devices).SelectionToken), host.Actor(SiteA), default));
        Assert.Equal("trial_socket_limit", refused.Code);
        Assert.Equal(before, await host.PersistedStateAsync());
    }

    [SqlServerFact]
    public async Task PaidAndTrialCoOwnersUseTheirOwnQuotaDespiteOtherCoOwnersExpiredTrial()
    {
        await using var host = await Host.StartAsync();
        await host.AddMembershipAsync(OwnerB, SiteA);
        var first = await host.CreateSavedAsync(SiteA, "Shared first provider");
        var second = await host.CreateSavedAsync(SiteA, "Shared second provider");
        var third = await host.CreateSavedAsync(SiteA, "Shared third provider");
        await host.AddPaidSubscriptionAsync(OwnerB);
        var service = host.Setup(SiteA, appleEnabled: true);
        async Task<IntegrationBindingDto> SelectAsync(IntegrationInstanceDto instance, string actor)
        {
            var draft = Change(instance);
            var discovered = await service.DiscoverAsync(instance.Id, draft, host.Actor(SiteA, actor), default);
            return await service.SelectDeviceAsync(instance.Id, new(draft, Assert.Single(discovered.Devices).SelectionToken), host.Actor(SiteA, actor), default);
        }
        var paidFirst = await SelectAsync(first, OwnerB);
        var paidSecond = await SelectAsync(second, OwnerB);
        var trialFirst = await SelectAsync(third, OwnerA);
        await using (var db = host.Factory(SiteA).CreateDbContext())
        {
            Assert.Equal(2, await db.IntegrationDeviceBindings.CountAsync(b => b.AddedByUserId == OwnerB));
            Assert.Equal(trialFirst.Id, (await db.IntegrationDeviceBindings.SingleAsync(b => b.AddedByUserId == OwnerA)).Id);
        }
        var fourth = await host.CreateSavedAsync(SiteA, "Shared fourth provider");
        var before = await host.PersistedStateAsync();
        var quota = await Assert.ThrowsAsync<IntegrationRequestException>(() => SelectAsync(fourth, OwnerA));
        Assert.Equal("trial_socket_limit", quota.Code);
        Assert.Equal(before, await host.PersistedStateAsync());
        await host.SetTrialAsync(OwnerA, host.Clock.Now.AddMonths(-1), host.Clock.Now);
        var paidThird = await SelectAsync(fourth, OwnerB);
        Assert.NotEqual(paidFirst.Id, paidThird.Id);
        Assert.NotEqual(paidSecond.Id, paidThird.Id);
        await using var check = host.Factory(SiteA).CreateDbContext();
        Assert.Equal(3, await check.IntegrationDeviceBindings.CountAsync(b => b.AddedByUserId == OwnerB));
        Assert.Equal(1, await check.IntegrationDeviceBindings.CountAsync(b => b.AddedByUserId == OwnerA));
    }

    [SqlServerFact]
    public async Task ExpiredCircuitCannotUseRetainedSocketCacheOrCommandReplayWhenCoOwnerIsPaid()
    {
        await using var host = await Host.StartAsync();
        await host.AddMembershipAsync(OwnerB, SiteA);
        await host.AddPaidSubscriptionAsync(OwnerB);
        var instance = await host.CreateSavedAsync(SiteA, "Shared socket provider");
        var device = await host.AddSocketAsync(SiteA, instance.Id);
        await host.EnableAsync(SiteA, instance.Id);
        await host.SetTrialAsync(OwnerA, host.Clock.Now.AddMonths(-1).AddSeconds(2), host.Clock.Now.AddSeconds(2));
        var gateway = host.Gateway(SiteA, appleEnabled: true);
        var current = new CurrentBillingAccount(); current.BindOnce(OwnerA);
        var circuit = host.Circuit(gateway, OwnerA, current);
        var socket = await circuit.GetAsync(new(device), default);
        var command = new SetSocketPowerCommand(new(Guid.NewGuid()), SwitchState.On);
        Assert.Equal(SocketCommandStatus.Acknowledged, (await socket.SetPowerAsync(command, default)).Status);
        Assert.Single((await circuit.ReadInventoryAsync(true, default)).Devices);
        var before = await host.PersistedStateAsync();
        var providerCalls = host.Executor.RuntimeCalls;
        host.Clock.Now = host.Clock.Now.AddSeconds(2);
        Assert.True(await host.Access(appleEnabled: true).InstallationHasAccessAsync(SiteA, default));
        Assert.False((await host.Access(appleEnabled: true).ReadAsync(OwnerA)).HasAccess);

        foreach (var denied in new Func<Task>[]
        {
            async () => await circuit.GetAsync(new(device), default),
            async () => await circuit.ReadInventoryAsync(false, default),
            async () => await circuit.GetCachedDevicesAsync(default),
            async () => await circuit.RefreshDevicesAsync(default),
            async () => await circuit.GetStateAsync(device.ToString("D"), default),
            () => circuit.TurnOnAsync(device.ToString("D"), default),
            () => circuit.TurnOffAsync(device.ToString("D"), default),
            async () => await circuit.ReadResultAsync(new(device), command.CommandId, default),
            async () => await circuit.ListUnresolvedAsync(new(device), default),
            async () => await circuit.ReleaseAsync(new(device), command.CommandId, default),
            async () => await socket.ReadAsync(default),
            async () => await socket.SetPowerAsync(command, default)
        })
            await Assert.ThrowsAsync<BillingAccessException>(denied);
        Assert.Equal(providerCalls, host.Executor.RuntimeCalls);
        Assert.Equal(before, await host.PersistedStateAsync());

        var payer = new CurrentBillingAccount(); payer.BindOnce(OwnerB);
        var paidCircuit = host.Circuit(gateway, OwnerB, payer);
        Assert.Single((await paidCircuit.ReadInventoryAsync(false, default)).Devices);
        Assert.Equal(providerCalls, host.Executor.RuntimeCalls);
        Assert.Throws<InvalidOperationException>(() => current.BindOnce(OwnerB));
    }

    [SqlServerFact]
    public async Task QueuedInteractiveCommandRechecksItsActorAfterExpiryEvenWhenCoOwnerKeepsInstallationPaid()
    {
        await using var host = await Host.StartAsync();
        await host.AddMembershipAsync(OwnerB, SiteA);
        await host.AddPaidSubscriptionAsync(OwnerB);
        var instance = await host.CreateSavedAsync(SiteA, "Queued shared socket");
        var device = await host.AddSocketAsync(SiteA, instance.Id);
        await host.EnableAsync(SiteA, instance.Id);
        await host.SetTrialAsync(OwnerA, host.Clock.Now.AddMonths(-1).AddSeconds(2), host.Clock.Now.AddSeconds(2));
        var gateway = host.Gateway(SiteA, appleEnabled: true);
        var account = new CurrentBillingAccount(); account.BindOnce(OwnerA);
        var circuit = host.Circuit(gateway, OwnerA, account);
        var socket = await circuit.GetAsync(new(device), default);
        var firstCommand = new SetSocketPowerCommand(new(Guid.NewGuid()), SwitchState.On);
        var queuedCommand = new SetSocketPowerCommand(new(Guid.NewGuid()), SwitchState.Off);
        host.Executor.PauseFirstSet = true;
        var first = socket.SetPowerAsync(firstCommand, default);
        await host.Executor.FirstSetStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var eligibleChecksComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        // Capture the eligible time for the adapter, installation and actor checks while the first send holds the command gate.
        host.Clock.ReadObserved = () => { if (Interlocked.Increment(ref checks) == 3) eligibleChecksComplete.TrySetResult(); };
        var queued = socket.SetPowerAsync(queuedCommand, default);
        try
        {
            await eligibleChecksComplete.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(queued.IsCompleted);
            host.Clock.ReadObserved = null;
            host.Clock.Now = host.Clock.Now.AddSeconds(2);
        }
        finally { host.Clock.ReadObserved = null; host.Executor.CompleteFirstSet.TrySetResult(); }
        Assert.Equal(SocketCommandStatus.Acknowledged, (await first.WaitAsync(TimeSpan.FromSeconds(15))).Status);
        await Assert.ThrowsAsync<BillingAccessException>(() => queued.WaitAsync(TimeSpan.FromSeconds(15)));
        await using (var db = host.Factory(SiteA).CreateDbContext())
            Assert.Equal(firstCommand.CommandId.Value, (await db.IntegrationCommands.SingleAsync()).Id);
        Assert.Equal(1, host.Executor.SetCalls);
        var payer = new CurrentBillingAccount(); payer.BindOnce(OwnerB);
        var paidSocket = await host.Circuit(gateway, OwnerB, payer).GetAsync(new(device), default);
        Assert.Equal(SocketCommandStatus.Acknowledged, (await paidSocket.SetPowerAsync(new(new(Guid.NewGuid()), SwitchState.Off), default)).Status);
        Assert.Equal(2, host.Executor.SetCalls);
    }

    [SqlServerFact]
    public async Task CookieWithoutAnActiveMembershipCanReadItsBillingAccountButCannotReadPrivateDevices()
    {
        foreach (var removeMembership in new[] { true, false })
        {
            await using var host = await Host.StartAsync();
            var instance = await host.CreateSavedAsync(SiteA, "Membership socket");
            await host.AddSocketAsync(SiteA, instance.Id);
            using var cookie = await host.CookieClientAsync(OwnerA);
            await using (var db = host.Factory().CreateDbContext())
            {
                if (removeMembership) db.InstallationMemberships.Remove(await db.InstallationMemberships.SingleAsync(m => m.UserId == OwnerA));
                else (await db.Installations.SingleAsync(i => i.Id == SiteA)).IsEnabled = false;
                await db.SaveChangesAsync();
            }
            var before = await host.PersistedStateAsync();
            using (var billing = await cookie.GetAsync("/api/billing/access"))
            {
                Assert.Equal(HttpStatusCode.OK, billing.StatusCode);
                Assert.True((await billing.Content.ReadFromJsonAsync<BillingAccess>())!.HasAccess);
            }
            using (var privateData = await cookie.GetAsync($"/api/v2/integrations/{instance.Id}/devices"))
                Assert.Equal(HttpStatusCode.Forbidden, privateData.StatusCode);
            using (var telemetry = await cookie.GetAsync("/api/devices?refresh=true"))
                Assert.Equal(HttpStatusCode.Forbidden, telemetry.StatusCode);
            Assert.Equal(0, host.DeviceReads);
            Assert.Equal(0, host.Executor.RuntimeCalls);
            Assert.Equal(before, await host.PersistedStateAsync());
        }
    }

    [SqlServerFact]
    public async Task UpgradeBackfillsEachHistoricalAccountOnceAndPreservesExistingSocketsAndData()
    {
        var database = await SqlServerTestDatabase.CreateAsync("SolarBillingUpgrade", SqlTestSchema.None);
        var options = database.Options;
        await using var db = new DeyeSolarDbContext(options);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("20261004010452_DynamicIntegrationOAuth");
            // Raw SQL represents the historical schema; current account helpers must not fill billing columns.
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO AspNetUsers (Id, UserName, NormalizedUserName, EmailConfirmed, PhoneNumberConfirmed,
                    TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
                VALUES ('old-owner-a', 'old-a', 'OLD-A', 0, 0, 0, 0, 0),
                    ('old-owner-b', 'old-b', 'OLD-B', 0, 0, 0, 0, 0);
                INSERT INTO InstallationMemberships (InstallationId, UserId, Role)
                VALUES ('legacy', 'old-owner-a', 'Owner'), ('legacy', 'old-owner-b', 'Owner');
                INSERT INTO AppSettings (Section, [Key], Value, InstallationId)
                VALUES ('Fixture', 'Preserve', 'historical value', 'legacy');
                INSERT INTO IntegrationInstances (Id, InstallationId, ProviderId, Name, PackageVersion,
                    PackageDigest, DescriptorDigest, ConfigurationVersion, Revision, Generation, State, CreatedAt, UpdatedAt)
                VALUES ('11111111-1111-1111-1111-111111111111', 'legacy', 'old-provider', 'Historical connection',
                    '1.0', 'old-package', 'old-descriptor', 1, 1, 1, 'enabled', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
                INSERT INTO IntegrationDeviceBindings (Id, InstallationId, InstanceId, RemoteId, Channel,
                    Kind, Name, MetadataJson, IsDefault, Enabled)
                VALUES ('22222222-2222-2222-2222-222222222222', 'legacy', '11111111-1111-1111-1111-111111111111',
                    'old-socket-a', '0', 'socket', 'Historical socket A', '{{}}', 0, 1),
                    ('33333333-3333-3333-3333-333333333333', 'legacy', '11111111-1111-1111-1111-111111111111',
                    'old-socket-b', '0', 'socket', 'Historical socket B', '{{}}', 0, 1);
                """);
            var historicalUsers = await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => new { u.Id, u.UserName, u.EmailConfirmed }).ToListAsync();
            var settingsBefore = await db.AppSettings.IgnoreQueryFilters().AsNoTracking().Select(row => new { row.Id, row.Section, row.Key, row.Value }).ToListAsync();
            var historicalSockets = db.IntegrationDeviceBindings.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new
                {
                    row.Id,
                    row.InstallationId,
                    row.InstanceId,
                    row.RemoteId,
                    row.Channel,
                    row.Kind,
                    row.Name,
                    row.MetadataJson,
                    row.IsDefault,
                    row.Enabled
                });
            var socketsBefore = JsonSerializer.Serialize(await historicalSockets.ToListAsync());
            var beforeMigration = await DatabaseTimeAsync(options);
            await db.Database.MigrateAsync();
            var afterMigration = await DatabaseTimeAsync(options);
            var accounts = await db.BillingAccounts.AsNoTracking().OrderBy(a => a.UserId).ToListAsync();
            Assert.Equal(2, accounts.Count);
            Assert.Equal(2, accounts.Select(a => a.AppAccountToken).Distinct().Count());
            Assert.DoesNotContain(accounts, a => a.AppAccountToken == Guid.Empty);
            foreach (var account in accounts)
            {
                Assert.InRange(account.TrialStartedAt, beforeMigration, afterMigration);
                Assert.Equal(account.TrialStartedAt.AddMonths(1), account.TrialEndsAt);
            }
            Assert.Single(accounts.Select(account => account.TrialStartedAt).Distinct());
            Assert.Equal(JsonSerializer.Serialize(historicalUsers), JsonSerializer.Serialize(await db.Users.AsNoTracking().OrderBy(u => u.Id)
                .Select(u => new { u.Id, u.UserName, u.EmailConfirmed }).ToListAsync()));
            Assert.Equal(JsonSerializer.Serialize(settingsBefore), JsonSerializer.Serialize(await db.AppSettings.IgnoreQueryFilters().AsNoTracking()
                .Select(row => new { row.Id, row.Section, row.Key, row.Value }).ToListAsync()));
            Assert.Equal(socketsBefore, JsonSerializer.Serialize(await historicalSockets.ToListAsync()));
            Assert.All(await db.IntegrationDeviceBindings.IgnoreQueryFilters().AsNoTracking().ToListAsync(), socket => Assert.Null(socket.AddedByUserId));
            var accountState = JsonSerializer.Serialize(accounts);
            await db.Database.MigrateAsync();
            Assert.Equal(accountState, JsonSerializer.Serialize(await db.BillingAccounts.AsNoTracking().OrderBy(a => a.UserId).ToListAsync()));
            var migrations = await db.Database.GetAppliedMigrationsAsync();
            var downgrade = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync("20261004010452_DynamicIntegrationOAuth"));
            Assert.Equal(51000, downgrade.Number);
            Assert.Equal(migrations, await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(accountState, JsonSerializer.Serialize(await db.BillingAccounts.AsNoTracking().OrderBy(a => a.UserId).ToListAsync()));
            Assert.Equal(socketsBefore, JsonSerializer.Serialize(await historicalSockets.ToListAsync()));
        }
        finally { await database.DisposeAsync(); }
    }

    [SqlServerFact]
    public async Task EmptyBillingSchemaCanDowngradeAndReapplyWithoutInventingAccounts()
    {
        var database = await SqlServerTestDatabase.CreateAsync("SolarBillingEmptyUpgrade", SqlTestSchema.None);
        var options = database.Options;
        await using var db = new DeyeSolarDbContext(options);
        try
        {
            var billingMigration = db.Database.GetMigrations().Single(migration => migration.EndsWith("_AccountBilling", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(billingMigration);
            var latest = await db.Database.GetAppliedMigrationsAsync();
            Assert.Empty(await db.BillingAccounts.ToListAsync());
            Assert.Empty(await db.AppleSubscriptions.ToListAsync());
            await db.GetService<IMigrator>().MigrateAsync("20261004010452_DynamicIntegrationOAuth");
            Assert.DoesNotContain((await db.Database.GetAppliedMigrationsAsync()), migration => migration.EndsWith("_AccountBilling", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(billingMigration);
            Assert.Equal(latest, await db.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await db.BillingAccounts.ToListAsync());
            Assert.Empty(await db.AppleSubscriptions.ToListAsync());
        }
        finally { await database.DisposeAsync(); }
    }

    private static IntegrationConfigurationChange Change(IntegrationInstanceDto instance) => new(instance.Revision,
        instance.PackageVersion, instance.PackageDigest, instance.DescriptorDigest, new(), new());
    private static async Task<DateTimeOffset> DatabaseTimeAsync(DbContextOptions<DeyeSolarDbContext> options)
    {
        await using var db = new DeyeSolarDbContext(options);
        return await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync();
    }
    private sealed record HttpLogin(string User, string Password, bool Cookie);

    private sealed class Host(WebApplication app, HttpClient client, DbContextOptions<DeyeSolarDbContext> options,
        Clock clock, Executor executor, IDataProtectionProvider protection, IntegrationSecretStore secrets,
        IntegrationChangeNotifier changes, IntegrationSetupGate gate, SqlServerTestDatabase database) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public Clock Clock { get; } = clock;
        public Executor Executor { get; } = executor;
        public string ConnectionString
        {
            get { using var db = Factory().CreateDbContext(); return db.Database.GetConnectionString()!; }
        }
        public int DeviceReads;
        public Factory Factory(string? installation = null, DbCommandInterceptor? interceptor = null) => new(
            interceptor is null ? options : new DbContextOptionsBuilder<DeyeSolarDbContext>(options).AddInterceptors(interceptor).Options, installation);
        public BillingAccessService Access(bool appleEnabled = false) => new(options, Clock, new AppleBillingOptions { Enabled = appleEnabled });
        private readonly Dictionary<string, ClaimsPrincipal> _actors = new();
        public ClaimsPrincipal Actor(string installation, string? user = null)
        {
            user ??= installation == SiteA ? OwnerA : OwnerB;
            var key = installation + ":" + user;
            if (_actors.TryGetValue(key, out var existing)) return existing;
            using var db = Factory().CreateDbContext();
            var stamp = db.Users.Single(u => u.Id == user).SecurityStamp!;
            var session = app.Services.GetRequiredService<MobileSessionStore>().Create(user, user, stamp, installation);
            return _actors[key] = new(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, user), new Claim(InstallationIds.ClaimType, installation),
                new Claim(InstallationAccessAuthorizer.StampClaim, stamp),
                new Claim(InstallationAccessAuthorizer.SessionClaim, session.Token)], "fixture-principal"));
        }
        public BillingSocketAccess Circuit(DynamicSocketGateway gateway, string user, CurrentBillingAccount account)
        {
            var installation = new CurrentInstallation(); installation.BindOnce(SiteA);
            var security = new InteractiveSecurityContext(new InstallationAccessAuthorizer(options,
                app.Services.GetRequiredService<IAccountSessionStore>(), Clock), installation, new HttpContextAccessor());
            security.BindOnce(Actor(SiteA, user));
            return new(gateway, gateway, gateway, gateway, gateway, gateway, Access(appleEnabled: true), account, security);
        }
        public IntegrationSetupService Setup(string installation, bool appleEnabled = false, DbCommandInterceptor? interceptor = null)
        {
            var current = new CurrentInstallation(); current.BindOnce(installation);
            var factory = Factory(installation, interceptor);
            return new(factory, new Catalog(), Executor, secrets, Clock, current, changes, gate,
                new IntegrationManagerAccess(new(factory), new FixtureInstallationAuthorizer(new(factory)), Access(appleEnabled)), new IntegrationConfigurationResolver(secrets),
                new IntegrationSelectionTokens(protection), new IntegrationDeviceBindingWriter(), new IntegrationConfigurationWriter(secrets, Clock, new IntegrationConnectionLifecycle(Clock)), new IntegrationConnectionLifecycle(Clock),
                billing: Access(appleEnabled), quota: new TrialSocketQuota(Access(appleEnabled)));
        }
        public DynamicSocketGateway Gateway(string installation, bool appleEnabled = false) => new(
            new IntegrationRegistry(Factory(installation), secrets), Executor, Factory(installation), Clock, Access(appleEnabled));
        public async Task AddMembershipAsync(string userId, string installation)
        {
            await using var db = Factory().CreateDbContext();
            db.InstallationMemberships.Add(new() { UserId = userId, InstallationId = installation, Role = "Owner" });
            await db.SaveChangesAsync();
        }
        public async Task AddPaidSubscriptionAsync(string userId)
        {
            await using var db = Factory().CreateDbContext();
            var account = await db.BillingAccounts.SingleAsync(a => a.UserId == userId);
            db.AppleSubscriptions.Add(new()
            {
                OriginalTransactionId = Guid.NewGuid().ToString("N"),
                UserId = userId,
                AppAccountToken = account.AppAccountToken,
                TransactionId = Guid.NewGuid().ToString("N"),
                ProductId = "com.dshapar.solar.monthly",
                Environment = "Production",
                Status = AppleSubscriptionStatus.Active,
                ExpiresAt = Clock.Now.AddMonths(2),
                SourceSignedAt = Clock.Now,
                CheckedAt = Clock.Now,
                ObservationStartedAt = Clock.Now
            });
            await db.SaveChangesAsync();
        }
        public async Task EnableAsync(string installation, Guid instanceId)
        {
            await using var db = Factory(installation).CreateDbContext();
            (await db.IntegrationInstances.SingleAsync(i => i.Id == instanceId)).State = "enabled";
            await db.SaveChangesAsync();
        }
        public async Task<IntegrationInstanceDto> CreateSavedAsync(string installation, string name)
        {
            var created = await Setup(installation).CreateAsync(new("billing.fixture", name), Actor(installation), default);
            return (await Setup(installation).SaveAsync(created.Id, Change(created), Actor(installation), default)).Instance;
        }
        public async Task<Guid> AddSocketAsync(string installation, Guid instanceId, string? addedByUserId = null)
        {
            await using var db = Factory(installation).CreateDbContext();
            var device = new IntegrationDeviceBindingEntity
            {
                Id = Guid.NewGuid(),
                InstanceId = instanceId,
                AddedByUserId = addedByUserId ?? (installation == SiteA ? OwnerA : OwnerB),
                Kind = "socket",
                RemoteId = "existing-socket",
                Channel = "0",
                Name = "Existing socket",
                MetadataJson = "{\"capabilities\":{\"canSwitch\":true}}"
            };
            db.IntegrationDeviceBindings.Add(device);
            db.TriggerRules.Add(new() { Name = "Existing rule", EntityId = device.Id.ToString("D"), SocTurnOnThreshold = 50 });
            db.Readings.Add(new() { Timestamp = DateTime.UtcNow, SolarDeviceSn = "neighbour-inverter", SolarProduction = 1234 });
            await db.SaveChangesAsync();
            return device.Id;
        }
        public async Task SetTrialAsync(string userId, DateTimeOffset start, DateTimeOffset end)
        {
            await using var db = Factory().CreateDbContext();
            var account = await db.BillingAccounts.SingleAsync(a => a.UserId == userId);
            account.TrialStartedAt = start;
            Assert.Equal(end, account.TrialEndsAt);
            await db.SaveChangesAsync();
        }
        public async Task<string> LoginAsync(string user)
        {
            using var response = await Client.PostAsJsonAsync("/_fixture/login", new HttpLogin(user, Password, false));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        }
        public HttpClient BearerClient(string token)
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { BaseAddress = Client.BaseAddress, Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
        public async Task<HttpClient> CookieClientAsync(string user)
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true })
            { BaseAddress = Client.BaseAddress, Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client.PostAsJsonAsync("/_fixture/login", new HttpLogin(user, Password, true));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return client;
        }
        public async Task<string> BillingStateAsync()
        {
            await using var db = Factory().CreateDbContext();
            return JsonSerializer.Serialize(new
            {
                Accounts = await db.BillingAccounts.AsNoTracking().OrderBy(a => a.UserId).ToListAsync(),
                Subscriptions = await db.AppleSubscriptions.AsNoTracking().OrderBy(a => a.OriginalTransactionId).ToListAsync()
            });
        }
        public async Task<string> InstallationStateAsync(string installation)
        {
            await using var db = Factory(installation).CreateDbContext();
            return JsonSerializer.Serialize(new
            {
                Instances = await db.IntegrationInstances.AsNoTracking().OrderBy(a => a.Id).ToListAsync(),
                Devices = await db.IntegrationDeviceBindings.AsNoTracking().OrderBy(a => a.Id).ToListAsync(),
                Configurations = await db.IntegrationConfigurations.AsNoTracking().OrderBy(a => a.InstanceId).ThenBy(a => a.Revision).ToListAsync(),
                Commands = await db.IntegrationCommands.AsNoTracking().OrderBy(a => a.Id).ToListAsync(),
                Rules = await db.TriggerRules.AsNoTracking().OrderBy(a => a.Id).ToListAsync(),
                Readings = await db.Readings.AsNoTracking().OrderBy(a => a.Id).ToListAsync(),
                Settings = await db.AppSettings.AsNoTracking().OrderBy(a => a.Id).ToListAsync()
            });
        }
        public async Task<string> PersistedStateAsync() => JsonSerializer.Serialize(new
        { Billing = await BillingStateAsync(), Own = await InstallationStateAsync(SiteA), Neighbour = await InstallationStateAsync(SiteB) });

        public static async Task<Host> StartAsync()
        {
            var database = await SqlServerTestDatabase.CreateAsync("SolarBillingTests");
            var options = database.Options;
            var clock = new Clock(); var executor = new Executor(clock);
            var protection = new EphemeralDataProtectionProvider();
            var secrets = new IntegrationSecretStore(new EphemeralDataProtectionProvider());
            var changes = new IntegrationChangeNotifier(NullLogger<IntegrationChangeNotifier>.Instance);
            var gate = new IntegrationSetupGate();
            WebApplication? app = null;
            try
            {
                await using (var db = new DeyeSolarDbContext(options))
                {
                    db.Installations.AddRange(new Installation { Id = SiteA, CreatedAt = clock.Now }, new Installation { Id = SiteB, CreatedAt = clock.Now });
                    foreach (var (id, site) in new[] { (OwnerA, SiteA), (OwnerB, SiteB) })
                    {
                        db.Users.Add(new() { Id = id, UserName = id, Email = id + "@example.test", EmailConfirmed = true });
                        db.InstallationMemberships.Add(new() { UserId = id, InstallationId = site });
                    }
                    await db.SaveChangesAsync();
                }
                clock.Now = DateTimeOffset.UtcNow;
                // A fixture expiry exactly two seconds ahead needs a day present in both adjacent calendar months.
                if (clock.Now.Day > 28) clock.Now = clock.Now.AddDays(32 - clock.Now.Day);
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
                builder.Services.AddScoped(_ => new DeyeSolarDbContext(options));
                builder.Services.AddIdentity<IdentityUser, IdentityRole>().AddEntityFrameworkStores<DeyeSolarDbContext>();
                builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MobileBearerAuthenticationHandler>(MobileBearerAuthenticationHandler.SchemeName, _ => { });
                builder.Services.AddAuthorization();
                builder.Services.AddAntiforgery();
                builder.Services.AddAccountIdentities(new AuthProviderOptions());
                builder.Services.AddSingleton(options);
                builder.Services.AddSingleton<TimeProvider>(clock);
                builder.Services.AddAppleBilling(new AppleBillingOptions());
                builder.Services.AddBillingAccess();
                builder.Services.AddScoped<CurrentBillingAccount>();
                builder.Services.AddScoped<MobileAuthService>();
                builder.Services.AddSingleton<MobileSessionStore>();
            builder.Services.AddSingleton<DeyeSolar.Web.Auth.IAccountSessionStore>(p => p.GetRequiredService<MobileSessionStore>());
                builder.Services.AddSingleton<IDataProtectionProvider>(protection);
                builder.Services.AddSingleton(secrets);
                builder.Services.AddSingleton(changes);
                builder.Services.AddSingleton(gate);
                builder.Services.AddScoped<IDbContextFactory<DeyeSolarDbContext>, RequestDbContextFactory>();
                builder.Services.AddSingleton<IIntegrationProviderCatalog>(new Catalog());
                builder.Services.AddSingleton<IIntegrationSetupExecutor>(executor);
                builder.Services.AddScoped<IInstallationAccessAuthorizer, InstallationAccessAuthorizer>();
                builder.Services.AddScoped<IIntegrationManagerAccess, IntegrationManagerAccess>();
            builder.Services.AddSingleton<IIntegrationConnectionLifecycle, IntegrationConnectionLifecycle>();
            builder.Services.AddSingleton<IIntegrationConfigurationWriter, IntegrationConfigurationWriter>();
            builder.Services.AddSingleton<IIntegrationConfigurationResolver, IntegrationConfigurationResolver>();
            builder.Services.AddSingleton<IIntegrationSelectionTokens, IntegrationSelectionTokens>();
            builder.Services.AddSingleton<IIntegrationDeviceBindingWriter, IntegrationDeviceBindingWriter>();
            builder.Services.AddScoped<IntegrationSetupService>();
                builder.Services.AddSingleton(new IntegrationOAuthOptions());
                builder.Services.AddSingleton<IOptions<IntegrationRuntimeOptions>>(Options.Create(new IntegrationRuntimeOptions()));
                builder.Services.AddSingleton<IntegrationOAuthService>();
                builder.Services.AddSingleton<IIntegrationPackageManager>(new DeniedPackageManager());
                    builder.Services.AddSingleton(provider => new TenantRuntimeFactory(options,
                    NullLoggerFactory.Instance, clock, provider.GetRequiredService<IHostApplicationLifetime>(), executor, secrets, changes));
                builder.Services.AddSingleton<TenantRuntimeRegistry>();
                builder.Services.AddScoped(provider => provider.GetRequiredService<TenantRuntimeRegistry>()
                    .Resolve<DynamicSocketGateway>(provider.GetRequiredService<CurrentInstallation>().Id!));
                builder.Services.AddScoped<UiText>();
                builder.Services.AddScoped<UserLanguageService>();
                app = builder.Build();
                app.UseRouting();
                app.UseRateLimiter();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseMiddleware<BillingAccessMiddleware>();
                app.UseMiddleware<InstallationBindingMiddleware>();
                app.MapDynamicIntegrations();
                app.MapAccountIdentityApi();
                app.MapAppleBilling();
                app.MapUserLanguage();
                app.MapPost("/_fixture/login", async (HttpLogin request, MobileAuthService auth, SignInManager<IdentityUser> signIn) =>
                {
                    var login = new MobileLoginRequest(request.User, request.Password);
                    var user = await auth.FindAndCheckPasswordAsync(login);
                    if (user is null) return Results.Unauthorized();
                    if (request.Cookie) await signIn.SignInAsync(user, false);
                    return Results.Ok(await auth.SignInAsync(login));
                }).AllowAnonymous();
                Host? host = null;
                // A handler counter proves the middleware prevents telemetry work before any device service is invoked.
                app.MapGet("/api/devices", () => { Interlocked.Increment(ref host!.DeviceReads); return Results.Ok(new { }); })
                    .RequireAuthorization(ApiAuthorization.AuthenticatedUser);
                using (var scope = app.Services.CreateScope())
                {
                    var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                    foreach (var id in new[] { OwnerA, OwnerB })
                    {
                        var user = (await users.FindByIdAsync(id))!;
                        Assert.True((await users.UpdateAsync(user)).Succeeded);
                        Assert.True((await users.AddPasswordAsync(user, Password)).Succeeded);
                    }
                }
                await app.StartAsync();
                var address = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
                Assert.Equal("127.0.0.1", address.Host);
                var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                { BaseAddress = address, Timeout = TimeSpan.FromSeconds(20) };
                host = new(app, client, options, clock, executor, protection, secrets, changes, gate, database);
                return host;
            }
            catch
            {
                await TestHttpHostCleanup.DisposeAsync(app, database);
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await TestHttpHostCleanup.DisposeAsync(app, database, stop: true);
        }
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options, string? installation) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => installation is null ? new(options) : new(options, installation);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }

    private sealed class BeforeInstanceLock : DbCommandInterceptor
    {
        private int entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Dispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[IntegrationInstances] WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal)
                && Interlocked.Exchange(ref entered, 1) == 0)
            {
                Entered.TrySetResult();
                await Dispatch.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class InstanceRowLock(SqlConnection connection, SqlTransaction transaction, int sessionId,
        string connectionString) : IAsyncDisposable
    {
        public static async Task<InstanceRowLock> AcquireAsync(string connectionString, Guid instanceId)
        {
            var connection = new SqlConnection(connectionString);
            SqlTransaction? transaction = null;
            try
            {
                await connection.OpenAsync();
                transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()";
                Assert.Equal(true, await command.ExecuteScalarAsync());
                command.CommandText = "SELECT [Id] FROM [IntegrationInstances] WITH (XLOCK, ROWLOCK, HOLDLOCK) WHERE [InstallationId] = @installation AND [Id] = @instance";
                command.Parameters.Add("@installation", SqlDbType.NVarChar, 64).Value = SiteA;
                command.Parameters.Add("@instance", SqlDbType.UniqueIdentifier).Value = instanceId;
                Assert.Equal(instanceId, await command.ExecuteScalarAsync());
                command.CommandText = "SELECT @@SPID";
                command.Parameters.Clear();
                return new(connection, transaction, Convert.ToInt32(await command.ExecuteScalarAsync()), connectionString);
            }
            catch
            {
                if (transaction is not null) await transaction.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public async Task WaitForBlockedSelectionAsync()
        {
            await using var observer = new SqlConnection(connectionString);
            await observer.OpenAsync();
            await using var command = observer.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = """
                SELECT COUNT(*) FROM sys.dm_exec_requests AS request
                CROSS APPLY sys.dm_exec_sql_text(request.sql_handle) AS statement
                WHERE request.database_id = DB_ID() AND request.blocking_session_id = @sessionId
                    AND request.wait_type LIKE N'LCK_M_%' AND CHARINDEX(N'IntegrationInstances', statement.text) > 0
                    AND CHARINDEX(N'UPDLOCK', statement.text) > 0
                """;
            command.Parameters.Add("@sessionId", SqlDbType.Int).Value = sessionId;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (Convert.ToInt32(await command.ExecuteScalarAsync(deadline.Token)) == 0)
                await Task.Delay(20, deadline.Token);
        }

        public async ValueTask DisposeAsync()
        {
            try { await transaction.RollbackAsync(); }
            finally { await transaction.DisposeAsync(); await connection.DisposeAsync(); }
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public Action? ReadObserved { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            var captured = Now;
            ReadObserved?.Invoke();
            return captured;
        }
    }
    private sealed class Catalog : IIntegrationProviderCatalog
    {
        private static IntegrationProviderDescriptor Descriptor => new("billing.fixture", "1.0", "billing-package", "billing-ui", "Billing test provider", 1, 1,
            ["text"], [], ["test", "discover"]);
        public Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationProviderDescriptor>>([Descriptor]);
        public Task<IntegrationProviderDescriptor> GetAsync(string providerId, string? version, CancellationToken ct) => Task.FromResult(Descriptor);
    }
    private sealed class Executor(Clock clock) : IIntegrationSetupExecutor, IIntegrationRuntimeExecutor
    {
        private int _calls;
        private int _runtimeCalls;
        private int _setCalls;
        public int Calls => _calls;
        public int RuntimeCalls => _runtimeCalls;
        public int SetCalls => _setCalls;
        public bool PauseFirstSet { get; set; }
        public TaskCompletionSource FirstSetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteFirstSet { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IntegrationTestResult> TestAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return Task.FromResult(new IntegrationTestResult(true, "ok", "Verified fixture.", "billing-fixture-account")); }
        public Task<IReadOnlyList<IntegrationDiscoveredDevice>> DiscoverAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, IntegrationDiscoveryQuery query, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IReadOnlyList<IntegrationDiscoveredDevice>>([new("discovered-socket", "0", "socket", "Trial socket", "billing-fixture-account",
                IntegrationJson.Element(new { capabilities = new { canSwitch = true } }))]);
        }
        public async Task<JsonElement> InvokeAsync(IntegrationSession session, string method, JsonElement parameters, CancellationToken ct)
        {
            Interlocked.Increment(ref _runtimeCalls);
            if (method == "socket.set" && Interlocked.Increment(ref _setCalls) == 1 && PauseFirstSet)
            {
                FirstSetStarted.TrySetResult();
                await CompleteFirstSet.Task.WaitAsync(ct);
            }
            return method switch
            {
                "socket.read" => IntegrationJson.Element(new ProviderSocketTelemetry(parameters.GetProperty("remoteId").GetString()!, "0",
                    true, true, 12, clock.Now, clock.Now)),
                "socket.set" => IntegrationJson.Element(new ProviderSocketCommandResult(parameters.GetProperty("commandId").GetString()!, "acknowledged", null)),
                _ => throw new InvalidOperationException("The billing fixture does not implement this provider method.")
            };
        }
        public Task StopAsync(Guid instanceId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class DeniedPackageManager : IIntegrationPackageManager
    {
        public Task<IntegrationInstalledPackage> InstallAsync(IntegrationPackageInstallRequest request, CancellationToken ct) => throw new InvalidOperationException("No packages are installed by billing tests.");
        public Task<IntegrationInstalledPackage> ResolveAsync(ProviderPackageIdentity identity, CancellationToken ct) => throw new NotSupportedException();
    }
}

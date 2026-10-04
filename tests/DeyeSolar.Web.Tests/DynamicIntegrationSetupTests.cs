using System.Security.Claims;
using System.Text.Json;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;
using Microsoft.Extensions.Options;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Tenancy;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor.Services;
using MudBlazor;

namespace DeyeSolar.Web.Tests;

public class DynamicIntegrationSetupTests
{
    [SqlServerFact]
    public async Task GenericWebFormRendersUnknownManufacturerAndProbePreservesSavedConfiguration()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateSavedAsync("site-a", "A");
        var before = await fixture.StateAsync();
        await using var services = fixture.ComponentServices(new Catalog());
        await using var renderer = new SetupRenderer(services, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            Assert.Contains("Extra setting", renderer.Text(root));
            Assert.Contains("Keep saved credential", renderer.Text(root));
            Assert.DoesNotContain("saved-A", renderer.Text(root));
            renderer.SetCredential("apiKey", "draft-web-only");
            await renderer.ClickAsync(root, "Test connection");
            Assert.Contains("Connection verified.", renderer.Text(root));
            Assert.DoesNotContain("draft-web-only", renderer.Text(root));
        });
        Assert.Equal(before, await fixture.StateAsync());
    }

    [SqlServerFact]
    public async Task WebCanDisableEnabledIntegrationEvenWhenItsProviderDescriptorIsUnavailable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateSavedAsync("site-a", "A");
        var enabled = await fixture.Service("site-a").SetEnabledAsync(instance.Id, true,
            new(instance.Revision, instance.PackageVersion, instance.PackageDigest, instance.DescriptorDigest), fixture.Actor("site-a"), default);
        await using var services = fixture.ComponentServices(new MissingDescriptorCatalog());
        await using var renderer = new SetupRenderer(services, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            await renderer.ClickAsync(root, "Disable A");
            Assert.Contains("Integration disabled.", renderer.Text(root));
        });
        var saved = await fixture.Service("site-a").ReadAsync(instance.Id, default);
        Assert.Equal("disabled", saved.Instance.Status);
        Assert.Equal(enabled.Revision, saved.Instance.Revision);
        Assert.Equal("saved-A", await fixture.SavedSecretAsync("site-a", instance.Id));
    }

    [SqlServerFact]
    public async Task WebCanDisableEnabledIntegrationWhenTheCatalogCannotVerifyAnInstalledPackage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateSavedAsync("site-a", "A");
        await fixture.CreateSavedAsync("site-b", "B");
        await fixture.Service("site-a").SetEnabledAsync(instance.Id, true,
            new(instance.Revision, instance.PackageVersion, instance.PackageDigest, instance.DescriptorDigest), fixture.Actor("site-a"), default);
        await using var services = fixture.ComponentServices(new ThrowingCatalog());
        await using var renderer = new SetupRenderer(services, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            Assert.DoesNotContain("No provider packages are installed", renderer.Text(root));
            await renderer.ClickAsync(root, "Disable A");
            Assert.Contains("Integration disabled.", renderer.Text(root));
        });
        Assert.Equal("disabled", (await fixture.Service("site-a").ReadAsync(instance.Id, default)).Instance.Status);
        Assert.Equal("saved-A", await fixture.SavedSecretAsync("site-a", instance.Id));
    }
    [SqlServerFact]
    public async Task RealIdentityBearerAndCookieRequestsEnforceTenantMembershipCsrfAndOperatorPermission()
    {
        await using var fixture = await Fixture.CreateAsync();
        var own = await fixture.CreateSavedAsync("site-a", "A");
        var neighbour = await fixture.CreateSavedAsync("site-b", "B");
        await using var app = await fixture.StartHttpAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        using (var anonymous = await client.GetAsync("/api/v2/integrations")) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v2/integrations"))
        {
            forged.Headers.Authorization = new("Bearer", "forged");
            using var denied = await client.SendAsync(forged);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        var token = await LoginAsync(client, "owner-a", false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var listed = await client.GetAsync("/api/v2/integrations"))
        {
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            Assert.Equal(own.Id, Assert.Single((await listed.Content.ReadFromJsonAsync<IntegrationInstanceDto[]>())!).Id);
        }
        using (var foreign = await client.GetAsync($"/api/v2/integrations/{neighbour.Id}/configuration")) Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using (var view = await client.GetAsync($"/api/v2/integrations/{own.Id}/configuration"))
        {
            Assert.Equal(HttpStatusCode.OK, view.StatusCode);
            Assert.DoesNotContain("saved-A", await view.Content.ReadAsStringAsync());
        }
        using (var deniedInstall = await client.PostAsJsonAsync("/api/v2/integration-packages/install", new IntegrationPackageInstallRequest("unused", "unused")))
            Assert.Equal(HttpStatusCode.Forbidden, deniedInstall.StatusCode);
        using (var catalog = await client.GetAsync("/api/v2/integration-providers"))
        {
            using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/v2/integration-providers");
            conditional.Headers.IfNoneMatch.Add(catalog.Headers.ETag!);
            using var unchanged = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        }
        var before = await fixture.StateAsync();
        await LoginAsync(client, "owner-a", true);
        using (var mixed = await client.PostAsJsonAsync($"/api/v2/integrations/{own.Id}/test", fixture.Change(own, "draft-http")))
            Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        using (var cookie = await client.PostAsJsonAsync($"/api/v2/integrations/{own.Id}/test", fixture.Change(own, "draft-http")))
            Assert.Equal(HttpStatusCode.BadRequest, cookie.StatusCode);
        Assert.Equal(before, await fixture.StateAsync());
        var csrf = await client.GetFromJsonAsync<JsonElement>("/_fixture/csrf");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", csrf.GetProperty("requestToken").GetString());
        using (var verified = await client.PostAsJsonAsync($"/api/v2/integrations/{own.Id}/test", fixture.Change(own, "draft-http")))
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.Equal(before, await fixture.StateAsync());
        await using (var db = fixture.Factory("site-a").CreateDbContext())
        {
            (await db.InstallationMemberships.SingleAsync(row => row.UserId == "owner-a")).Role = "Reader";
            await db.SaveChangesAsync();
        }
        var calls = fixture.Executor.Calls;
        using (var revokedPermission = await client.PutAsJsonAsync($"/api/v2/integrations/{own.Id}/configuration", fixture.Change(own, "forbidden-http")))
            Assert.Equal(HttpStatusCode.Forbidden, revokedPermission.StatusCode);
        Assert.Equal(calls, fixture.Executor.Calls);
        Assert.Equal(before, await fixture.StateAsync());
        Assert.Equal("saved-A", await fixture.SavedSecretAsync("site-a", own.Id));
        Assert.Equal("saved-B", await fixture.SavedSecretAsync("site-b", neighbour.Id));
    }

    private static async Task<string> LoginAsync(HttpClient client, string user, bool cookie)
    {
        using var login = await client.PostAsJsonAsync("/_fixture/login", new HttpLogin(user, "LocalDynamic!42", cookie));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }
    private sealed record HttpLogin(string User, string Password, bool Cookie);
    [Fact]
    public void OlderRuleRequestsPreserveSourceWhileExplicitNullSelectsPrimary()
    {
        var source = Guid.NewGuid();
        var existing = new TriggerRule { SourceInverterId = source };
        const string json = "{\"name\":\"Rule\",\"entityId\":\"socket\",\"enabled\":false}";
        var old = JsonSerializer.Deserialize<TriggerRuleRequest>(json, IntegrationJson.Options)!;
        Assert.False(old.SourceInverterSpecified);
        Assert.Equal(source, old.ToRule(existing).SourceInverterId);
        var primary = JsonSerializer.Deserialize<TriggerRuleRequest>(json[..^1] + ",\"sourceInverterId\":null}", IntegrationJson.Options)!;
        Assert.True(primary.SourceInverterSpecified);
        Assert.Null(primary.ToRule(existing).SourceInverterId);
        var explicitSource = Guid.NewGuid();
        Assert.Equal(explicitSource, (old with { SourceInverterId = explicitSource }).ToRule(existing).SourceInverterId);
    }

    [SqlServerFact]
    public async Task RuleSourcesValidateTenantEnabledStateAndCapabilitiesWithoutChangingNeighbours()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateSavedAsync("site-a", "A");
        var neighbour = await fixture.CreateSavedAsync("site-b", "B");
        var selected = await fixture.AddInverterAsync("site-a", first.Id, true, true);
        var foreign = await fixture.AddInverterAsync("site-b", neighbour.Id, true, true);
        var repository = new RuleRepository(fixture.Factory("site-a"));
        var rule = await repository.CreateAsync(new TriggerRule { Name = "Own rule", EntityId = "own-socket", Enabled = true, SourceInverterId = selected }, default);
        var before = await fixture.StateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateAsync(new TriggerRule { Name = "Forbidden", Enabled = true, SourceInverterId = foreign }, default));
        rule.SourceInverterId = foreign;
        await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateAsync(rule, default));
        Assert.Equal(before, await fixture.StateAsync());
        await using var check = fixture.Factory("site-a").CreateDbContext();
        var saved = await check.TriggerRules.AsNoTracking().SingleAsync();
        Assert.Equal(selected, saved.SourceInverterId);
        Assert.Equal("own-socket", saved.EntityId);
        Assert.Empty(await check.TriggerRules.IgnoreQueryFilters().Where(row => row.InstallationId == "site-b").ToListAsync());
    }

    [SqlServerFact]
    public async Task SourceWithoutRequiredMeasurementsCannotEnableButCanBeStoppedAfterOutage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateSavedAsync("site-a", "A");
        var noBattery = await fixture.AddInverterAsync("site-a", instance.Id, false, true);
        var noSolar = await fixture.AddInverterAsync("site-a", instance.Id, true, false);
        var repository = new RuleRepository(fixture.Factory("site-a"));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateAsync(new TriggerRule { Name = "No SOC", Enabled = true, SourceInverterId = noBattery }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateAsync(new TriggerRule { Name = "No PV", Enabled = true, UseSolarProductionThreshold = true, SourceInverterId = noSolar }, default));
        var rule = await repository.CreateAsync(new TriggerRule { Name = "Battery rule", Enabled = true, SourceInverterId = noSolar }, default);
        await using (var db = fixture.Factory("site-a").CreateDbContext())
        {
            (await db.IntegrationInstances.SingleAsync()).State = "disabled";
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateAsync(rule, default));
        rule.Enabled = false;
        await repository.UpdateAsync(rule, default);
        var saved = await repository.GetByIdAsync(rule.Id, default);
        Assert.False(saved!.Enabled);
        Assert.Equal(noSolar, saved.SourceInverterId);
        await repository.CreateAsync(new TriggerRule { Name = "Legacy primary", Enabled = true }, default);
    }
    [Fact]
    public void EncryptedCredentialsAreBoundToInstallationInstanceAndRevision()
    {
        var storage = new IntegrationSecretStore(new EphemeralDataProtectionProvider());
        var instance = Guid.NewGuid();
        var ciphertext = storage.Encrypt("one", instance, 3, new Dictionary<string, string> { ["key"] = "fixture-sensitive-value" });
        Assert.DoesNotContain("fixture-sensitive-value", ciphertext);
        Assert.Equal("fixture-sensitive-value", storage.Decrypt("one", instance, 3, ciphertext)["key"]);
        Assert.Throws<IntegrationRequestException>(() => storage.Decrypt("two", instance, 3, ciphertext));
        Assert.Throws<IntegrationRequestException>(() => storage.Decrypt("one", Guid.NewGuid(), 3, ciphertext));
        Assert.Throws<IntegrationRequestException>(() => storage.Decrypt("one", instance, 4, ciphertext));
    }

    [SqlServerFact]
    public async Task WorkerDiagnosticsAndMetadataCannotEchoCredentialsIntoPublicResponsesOrPlaintextStorage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateSavedAsync("site-a", "A");
        fixture.Executor.TestMessage = "saved-A";
        var tested = await fixture.Service("site-a").TestAsync(instance.Id, fixture.Change(instance, null), fixture.Actor("site-a"), default);
        Assert.True(tested.Success);
        Assert.DoesNotContain("saved-A", JsonSerializer.Serialize(tested));
        fixture.Executor.Devices = [new("public-remote", "0", "socket", "Public name", "verified-account",
            IntegrationJson.Element(new { apiKey = "saved-A", capabilities = new { canSwitch = true, password = "saved-A" } }))];
        var discovery = await fixture.Service("site-a").DiscoverAsync(instance.Id, fixture.Change(instance, null), fixture.Actor("site-a"), default);
        Assert.DoesNotContain("saved-A", JsonSerializer.Serialize(discovery));
        var binding = await fixture.Service("site-a").SelectDeviceAsync(instance.Id, new(fixture.Change(instance, null), discovery.Devices.Single().SelectionToken), fixture.Actor("site-a"), default);
        await using var db = fixture.Factory("site-a").CreateDbContext();
        var metadata = (await db.IntegrationDeviceBindings.SingleAsync(b => b.Id == binding.Id)).MetadataJson;
        Assert.Contains("canSwitch", metadata);
        Assert.DoesNotContain("saved-A", metadata);
    }

    [SqlServerFact]
    public async Task ExplicitPackageUpgradeAndRollbackPreserveBindingsAndNeighbourAndRejectIncompatibleSchema()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateSavedAsync("site-a", "A");
        var neighbour = await fixture.CreateSavedAsync("site-b", "B");
        var draft = fixture.Change(instance, null);
        var discovered = await fixture.Service("site-a").DiscoverAsync(instance.Id, draft, fixture.Actor("site-a"), default);
        var binding = await fixture.Service("site-a").SelectDeviceAsync(instance.Id, new(draft, discovered.Devices.Single().SelectionToken), fixture.Actor("site-a"), default);
        var before = await fixture.StateAsync();
        IntegrationPackageChange Change(IntegrationInstanceDto value, string version) => new(new(value.Revision,
            value.PackageVersion, value.PackageDigest, value.DescriptorDigest), version);
        var incompatible = await Assert.ThrowsAsync<IntegrationRequestException>(() => fixture.Service("site-a")
            .SwitchPackageAsync(instance.Id, Change(instance, "2.0"), fixture.Actor("site-a"), default));
        Assert.Equal("configuration_migration_required", incompatible.Code);
        Assert.Equal(before, await fixture.StateAsync());
        var upgraded = await fixture.Service("site-a").SwitchPackageAsync(instance.Id, Change(instance, "1.1"), fixture.Actor("site-a"), default);
        Assert.Equal("1.1", upgraded.PackageVersion);
        Assert.True(upgraded.Revision > instance.Revision);
        Assert.True(upgraded.Generation > instance.Generation);
        Assert.Equal("default-extra", (await fixture.Service("site-a").ReadAsync(instance.Id, default)).Values["addedSetting"].GetString());
        Assert.Equal("saved-A", await fixture.SavedSecretAsync("site-a", instance.Id));
        var rolledBack = await fixture.Service("site-a").SwitchPackageAsync(instance.Id, Change(upgraded, "1.0"), fixture.Actor("site-a"), default);
        Assert.Equal("1.0", rolledBack.PackageVersion);
        Assert.False((await fixture.Service("site-a").ReadAsync(instance.Id, default)).Values.ContainsKey("addedSetting"));
        Assert.Equal(binding.Id, Assert.Single(await fixture.Service("site-a").DevicesAsync(instance.Id, default)).Id);
        Assert.Equal(neighbour, (await fixture.Service("site-b").ReadAsync(neighbour.Id, default)).Instance);
        Assert.Equal("saved-B", await fixture.SavedSecretAsync("site-b", neighbour.Id));
        var final = await fixture.StateAsync();
        await Assert.ThrowsAsync<IntegrationRequestException>(() => fixture.Service("site-a").SwitchPackageAsync(instance.Id, Change(upgraded, "1.0"), fixture.Actor("site-a"), default));
        Assert.Equal(final, await fixture.StateAsync());
    }

    [SqlServerFact]
    public async Task ConcurrentDurableCommandIntentPreventsPackageReplacementWithoutChangingSettingsOrNeighbours()
    {
        const string status = "pending";
        await using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.CreateSavedAsync("site-a", "A");
        var neighbour = await fixture.CreateSavedAsync("site-b", "B");
        var draft = fixture.Change(saved, null);
        var found = await fixture.Service("site-a").DiscoverAsync(saved.Id, draft, fixture.Actor("site-a"), default);
        var binding = await fixture.Service("site-a").SelectDeviceAsync(saved.Id, new(draft, found.Devices.Single().SelectionToken), fixture.Actor("site-a"), default);
        var enabled = await fixture.Service("site-a").SetEnabledAsync(saved.Id, true,
            new(saved.Revision, saved.PackageVersion, saved.PackageDigest, saved.DescriptorDigest), fixture.Actor("site-a"), default);
        var before = await fixture.StateAsync();
        await using var commandDb = fixture.Factory("site-a").CreateDbContext();
        await using var transaction = await commandDb.Database.BeginTransactionAsync();
        await commandDb.IntegrationInstances.FromSqlInterpolated($"SELECT * FROM [IntegrationInstances] WITH (UPDLOCK, HOLDLOCK) WHERE [InstallationId] = {"site-a"} AND [Id] = {enabled.Id}")
            .AsNoTracking().SingleAsync();
        var command = new IntegrationCommandEntity
        {
            Id = Guid.NewGuid(),
            InstanceId = enabled.Id,
            DeviceId = binding.Id,
            Revision = enabled.Revision,
            Generation = enabled.Generation,
            Status = status,
            DesiredState = true,
            ProviderOperationId = status == "pending" ? "fixture-operation" : null,
            PayloadHash = "fixture-payload",
            CreatedAt = DateTimeOffset.UtcNow
        };
        commandDb.IntegrationCommands.Add(command);
        await commandDb.SaveChangesAsync();
        fixture.Executor.TestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacing = fixture.Service("site-a").SwitchPackageAsync(enabled.Id,
            new(new(enabled.Revision, enabled.PackageVersion, enabled.PackageDigest, enabled.DescriptorDigest), "1.1"), fixture.Actor("site-a"), default);
        await fixture.Executor.TestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await transaction.CommitAsync();
        var error = await Assert.ThrowsAsync<IntegrationRequestException>(() => replacing);
        Assert.Equal("active_commands", error.Code);
        Assert.Equal(before, await fixture.StateAsync());
        Assert.Equal(neighbour, (await fixture.Service("site-b").ReadAsync(neighbour.Id, default)).Instance);
        Assert.Equal("saved-A", await fixture.SavedSecretAsync("site-a", enabled.Id));
        await using var check = fixture.Factory("site-a").CreateDbContext();
        var retained = await check.IntegrationCommands.SingleAsync();
        Assert.Equal(command.Id, retained.Id);
        Assert.Equal(status, retained.Status);
        Assert.Equal(enabled.Generation, retained.Generation);
    }

    [SqlServerFact]
    public async Task ActiveCommandsProtectEveryGenerationMutationButNeverPreventDisableAndDoNotBlockOtherInstances()
    {
        await using var fixture = await Fixture.CreateAsync();
        var own = await fixture.CreateSavedAsync("site-a", "A");
        var sibling = await fixture.CreateSavedAsync("site-a", "Other");
        var foreign = await fixture.CreateSavedAsync("site-b", "B");
        IntegrationDiscoveryResponse? ownDiscovery = null;
        var commands = new List<Guid>();
        foreach (var (installation, saved) in new[] { ("site-a", own), ("site-a", sibling), ("site-b", foreign) })
        {
            var discovery = await fixture.Service(installation).DiscoverAsync(saved.Id, fixture.Change(saved, null), fixture.Actor(installation), default);
            var device = await fixture.Service(installation).SelectDeviceAsync(saved.Id, new(fixture.Change(saved, null), discovery.Devices.Single().SelectionToken), fixture.Actor(installation), default);
            var enabled = await fixture.Service(installation).SetEnabledAsync(saved.Id, true,
                new(saved.Revision, saved.PackageVersion, saved.PackageDigest, saved.DescriptorDigest), fixture.Actor(installation), default);
            var id = Guid.NewGuid(); commands.Add(id);
            await using var seed = fixture.Factory(installation).CreateDbContext();
            seed.IntegrationCommands.Add(new()
            {
                Id = id,
                InstanceId = saved.Id,
                DeviceId = device.Id,
                Revision = enabled.Revision,
                Generation = enabled.Generation,
                Status = saved.Id == own.Id ? "requested" : "pending",
                DesiredState = true,
                PayloadHash = "fixture-intent",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
            if (saved.Id == own.Id) { own = enabled; ownDiscovery = discovery; }
        }
        var beforeSelection = await fixture.StateAsync();
        var selectedError = await Assert.ThrowsAsync<IntegrationRequestException>(() => fixture.Service("site-a").SelectDeviceAsync(own.Id,
            new(fixture.Change(own, null), ownDiscovery!.Devices.Single().SelectionToken), fixture.Actor("site-a"), default));
        Assert.Equal("active_commands", selectedError.Code);
        Assert.Equal(beforeSelection, await fixture.StateAsync());
        var disabled = await fixture.Service("site-a").SetEnabledAsync(own.Id, false,
            new(own.Revision, own.PackageVersion, own.PackageDigest, own.DescriptorDigest), fixture.Actor("site-a"), default);
        Assert.Equal("disabled", disabled.Status);
        await using (var verifyRetirement = fixture.Factory("site-a").CreateDbContext())
        {
            var retired = await verifyRetirement.IntegrationCommands.SingleAsync(command => command.Id == commands[0]);
            Assert.Equal("uncertain", retired.Status);
            Assert.Equal("retired_generation", retired.ErrorCode);
            Assert.NotNull(retired.CompletedAt);
            Assert.Equal(own.Generation, retired.Generation);
            // A historical disabled instance may retain a known active receipt from an older host.
            retired.Status = "requested";
            await verifyRetirement.SaveChangesAsync();
        }
        var before = await fixture.StateAsync();
        var service = fixture.Service("site-a");
        foreach (var mutation in new Func<Task>[]
        {
            () => service.SaveAsync(own.Id, fixture.Change(disabled, "replacement-after-result"), fixture.Actor("site-a"), default),
            () => service.SwitchPackageAsync(own.Id, new(new(disabled.Revision, disabled.PackageVersion, disabled.PackageDigest, disabled.DescriptorDigest), "1.1"), fixture.Actor("site-a"), default),
            () => service.SetEnabledAsync(own.Id, true, new(disabled.Revision, disabled.PackageVersion, disabled.PackageDigest, disabled.DescriptorDigest), fixture.Actor("site-a"), default)
        })
        {
            Assert.Equal("active_commands", (await Assert.ThrowsAsync<IntegrationRequestException>(mutation)).Code);
            Assert.Equal(before, await fixture.StateAsync());
        }
        await using (var terminal = fixture.Factory("site-a").CreateDbContext())
        {
            (await terminal.IntegrationCommands.SingleAsync(command => command.Id == commands[0])).Status = "uncertain";
            await terminal.SaveChangesAsync();
        }
        var savedOwn = await service.SaveAsync(own.Id, fixture.Change(disabled, "replacement-after-result"), fixture.Actor("site-a"), default);
        Assert.True(savedOwn.Instance.Revision > disabled.Revision);
        var reenabled = await service.SetEnabledAsync(own.Id, true, new(savedOwn.Instance.Revision, savedOwn.Instance.PackageVersion,
            savedOwn.Instance.PackageDigest, savedOwn.Instance.DescriptorDigest), fixture.Actor("site-a"), default);
        Assert.Equal("enabled", reenabled.Status);
        Assert.Equal("replacement-after-result", await fixture.SavedSecretAsync("site-a", own.Id));
        Assert.Equal("saved-Other", await fixture.SavedSecretAsync("site-a", sibling.Id));
        Assert.Equal("saved-B", await fixture.SavedSecretAsync("site-b", foreign.Id));
        await using var check = fixture.Factory("site-a").CreateDbContext();
        var retained = await check.IntegrationCommands.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(3, retained.Count);
        Assert.Equal("uncertain", retained.Single(command => command.Id == commands[0]).Status);
        Assert.All(retained.Where(command => command.Id != commands[0]), command => Assert.Equal("pending", command.Status));
        Assert.Equal(sibling.Revision, (await service.ReadAsync(sibling.Id, default)).Instance.Revision);
        Assert.Equal(foreign.Revision, (await fixture.Service("site-b").ReadAsync(foreign.Id, default)).Instance.Revision);
    }

    [SqlServerFact]
    public async Task WebVersionSwitchUsesSavedCredentialsAndDiscardsDraftOnlyAfterSuccessfulSwitch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateSavedAsync("site-a", "A");
        await using var services = fixture.ComponentServices(new Catalog());
        await using var renderer = new SetupRenderer(services, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            renderer.SetCredential("apiKey", "unsaved-web-key");
            renderer.SetVersion("2.0");
            await renderer.ClickAsync(root, "Use selected version (discard draft)");
            Assert.Contains("Create and verify a new connection", renderer.Text(root));
            Assert.Equal("unsaved-web-key", renderer.CredentialValue("apiKey"));
            renderer.SetVersion("1.1");
            await renderer.ClickAsync(root, "Use selected version (discard draft)");
            Assert.Contains("draft discarded", renderer.Text(root));
            Assert.Equal("", renderer.CredentialValue("apiKey"));
        });
        Assert.Equal("1.1", (await fixture.Service("site-a").ReadAsync(instance.Id, default)).Instance.PackageVersion);
        Assert.Equal("saved-A", await fixture.SavedSecretAsync("site-a", instance.Id));
    }

    [SqlServerFact]
    public async Task ConcurrentSelectionOfTheSameDiscoveryResultReturnsOnePersistentBinding()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateSavedAsync("site-a", "A");
        var draft = fixture.Change(instance, null);
        var found = await fixture.Service("site-a").DiscoverAsync(instance.Id, draft, fixture.Actor("site-a"), default);
        var request = new SelectIntegrationDeviceRequest(draft, found.Devices.Single().SelectionToken);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Service("site-a")
            .SelectDeviceAsync(instance.Id, request, fixture.Actor("site-a"), default)));
        Assert.Single(results.Select(result => result.Id).Distinct());
        await using var db = fixture.Factory("site-a").CreateDbContext();
        Assert.Equal(results[0].Id, (await db.IntegrationDeviceBindings.SingleAsync()).Id);
        Assert.Equal("saved-A", await fixture.SavedSecretAsync("site-a", instance.Id));
    }

    [SqlServerFact]
    public async Task DraftTestAndDiscoveryDoNotSaveCredentialsSelectDevicesOrChangeNeighbour()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateSavedAsync("site-a", "A");
        var neighbour = await fixture.CreateSavedAsync("site-b", "B");
        var before = await fixture.StateAsync();
        var draft = fixture.Change(first, "changed-only-in-draft");
        Assert.True((await fixture.Service("site-a").TestAsync(first.Id, draft, fixture.Actor("site-a"), default)).Success);
        Assert.Single((await fixture.Service("site-a").DiscoverAsync(first.Id, draft, fixture.Actor("site-a"), default)).Devices);
        Assert.Equal(before, await fixture.StateAsync());
        Assert.Equal(neighbour.Revision, (await fixture.Service("site-b").ReadAsync(neighbour.Id, default)).Instance.Revision);
    }

    [SqlServerFact]
    public async Task OperatorSetupDeadlineBoundsChecksAndDiscoveryAndCallerCancellationIsPreserved()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateSavedAsync("site-a", "A");
        await fixture.CreateSavedAsync("site-b", "B");
        var before = await fixture.StateAsync();
        var service = fixture.Service("site-a", new() { RequestTimeoutSeconds = 1, MaximumNegotiatedRequestTimeoutSeconds = 1 });
        fixture.Executor.Check = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("A hanging provider check must be canceled.");
        };
        var result = await service.TestAsync(instance.Id, fixture.Change(instance, null), fixture.Actor("site-a"), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        fixture.Executor.Discovery = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("A hanging provider discovery must be canceled.");
        };
        var failure = await Assert.ThrowsAsync<IntegrationRequestException>(() => service.DiscoverAsync(instance.Id, fixture.Change(instance, null),
            fixture.Actor("site-a"), default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("unavailable", failure.Code);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TestAsync(instance.Id, fixture.Change(instance, null), fixture.Actor("site-a"), canceled.Token));
        Assert.Equal(before, await fixture.StateAsync());
    }

    [SqlServerFact]
    public async Task VersionConflictAndInvalidBatchHaveNoPersistedPartialEffect()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateSavedAsync("site-a", "A");
        var before = await fixture.StateAsync();
        var change = fixture.Change(instance, "replacement");
        var forged = change with { DescriptorDigest = "wrong-version" };
        Assert.Equal("configuration_conflict", (await Assert.ThrowsAsync<IntegrationRequestException>(() => fixture.Service("site-a").SaveAsync(instance.Id, forged, fixture.Actor("site-a"), default))).Code);
        change.Values["region"] = IntegrationJson.Element("unlisted-region");
        await Assert.ThrowsAsync<IntegrationRequestException>(() => fixture.Service("site-a").SaveAsync(instance.Id, change, fixture.Actor("site-a"), default));
        Assert.Equal(before, await fixture.StateAsync());
    }

    [SqlServerFact]
    public async Task SavedSecretsNeverAppearInPublicReadAndKeepClearReplacePersistExactly()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateSavedAsync("site-a", "A");
        var view = await fixture.Service("site-a").ReadAsync(instance.Id, default);
        Assert.True(view.SecretPresent["apiKey"]);
        Assert.DoesNotContain("saved-A", JsonSerializer.Serialize(view));
        var replacement = await fixture.Service("site-a").SaveAsync(instance.Id, fixture.Change(instance, "replacement-value"), fixture.Actor("site-a"), default);
        await using (var db = fixture.Factory("site-a").CreateDbContext())
        {
            var stored = await db.IntegrationConfigurations.SingleAsync(c => c.InstanceId == instance.Id && c.Revision == replacement.Instance.Revision);
            Assert.DoesNotContain("replacement-value", stored.SecretsCiphertext);
            Assert.DoesNotContain("replacement-value", stored.ValuesJson);
            Assert.Equal("replacement-value", fixture.Secrets.Decrypt("site-a", instance.Id, stored.Revision, stored.SecretsCiphertext)["apiKey"]);
        }
        var kept = fixture.Change(replacement.Instance, null);
        var keptView = await fixture.Service("site-a").SaveAsync(instance.Id, kept, fixture.Actor("site-a"), default);
        Assert.Equal(replacement.Instance.Revision, keptView.Instance.Revision);
        var cleared = kept with { SecretOperations = new() { ["apiKey"] = new("clear") } };
        await Assert.ThrowsAsync<IntegrationRequestException>(() => fixture.Service("site-a").SaveAsync(instance.Id, cleared, fixture.Actor("site-a"), default));
        Assert.Equal("replacement-value", await fixture.SavedSecretAsync("site-a", instance.Id));
    }

    [SqlServerFact]
    public async Task SelectionRequiresSavedDraftAndProofCannotCrossInstallationOrInstance()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateSavedAsync("site-a", "A");
        var second = await fixture.CreateSavedAsync("site-a", "Other");
        var neighbour = await fixture.CreateSavedAsync("site-b", "B");
        var dirty = fixture.Change(first, "unsaved");
        var discovered = await fixture.Service("site-a").DiscoverAsync(first.Id, dirty, fixture.Actor("site-a"), default);
        var token = discovered.Devices.Single().SelectionToken;
        Assert.Equal("save_before_selection", (await Assert.ThrowsAsync<IntegrationRequestException>(() => fixture.Service("site-a").SelectDeviceAsync(first.Id, new(dirty, token), fixture.Actor("site-a"), default))).Code);
        await Assert.ThrowsAsync<IntegrationRequestException>(() => fixture.Service("site-a").SelectDeviceAsync(second.Id, new(fixture.Change(second, null), token), fixture.Actor("site-a"), default));
        await Assert.ThrowsAsync<IntegrationRequestException>(() => fixture.Service("site-b").SelectDeviceAsync(neighbour.Id, new(fixture.Change(neighbour, null), token), fixture.Actor("site-b"), default));
        await using var check = fixture.Factory("site-a").CreateDbContext();
        Assert.Empty(await check.IntegrationDeviceBindings.IgnoreQueryFilters().ToListAsync());
    }

    [SqlServerFact]
    public async Task IdenticalRemoteIdsAcrossAccountsAndChannelsRemainIndependentAndReplayCreatesNoDuplicate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateSavedAsync("site-a", "A");
        var second = await fixture.CreateSavedAsync("site-a", "Other");
        var neighbour = await fixture.CreateSavedAsync("site-b", "B");
        var bindings = new List<IntegrationBindingDto>();
        foreach (var (installation, instance) in new[] { ("site-a", first), ("site-a", second), ("site-b", neighbour) })
        {
            fixture.Executor.Devices = [new("OpaqueCase", "0", "socket", "Relay zero", "verified-account"), new("OpaqueCase", "1", "socket", "Relay one", "verified-account"), new("opaquecase", "0", "socket", "Different opaque ID", "verified-account")];
            var draft = fixture.Change(instance, null);
            var found = await fixture.Service(installation).DiscoverAsync(instance.Id, draft, fixture.Actor(installation), default);
            foreach (var device in found.Devices)
            {
                var selected = await fixture.Service(installation).SelectDeviceAsync(instance.Id, new(draft, device.SelectionToken), fixture.Actor(installation), default);
                bindings.Add(selected);
                Assert.Equal(selected.Id, (await fixture.Service(installation).SelectDeviceAsync(instance.Id, new(draft, device.SelectionToken), fixture.Actor(installation), default)).Id);
            }
        }
        Assert.Equal(9, bindings.Select(b => b.Id).Distinct().Count());
        await using var check = fixture.Factory("site-a").CreateDbContext();
        Assert.Equal(6, await check.IntegrationDeviceBindings.CountAsync());
        Assert.Equal(9, await check.IntegrationDeviceBindings.IgnoreQueryFilters().CountAsync());
    }

    [SqlServerFact]
    public async Task ForgedMembershipAndUnknownInstanceCannotPerformSetupCallsOrMutateData()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateSavedAsync("site-a", "A");
        var before = await fixture.StateAsync();
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner-b"), new Claim(InstallationIds.ClaimType, "site-a")], "synthetic-test"));
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<IntegrationRequestException>(() => fixture.Service("site-a").TestAsync(instance.Id, fixture.Change(instance, null), forged, default))).Code);
        await Assert.ThrowsAsync<IntegrationRequestException>(() => fixture.Service("site-b").SaveAsync(instance.Id, fixture.Change(instance, "not-allowed"), fixture.Actor("site-b"), default));
        Assert.Equal(0, fixture.Executor.Calls);
        Assert.Equal(before, await fixture.StateAsync());
    }

    [SqlServerFact]
    public async Task ExplicitInverterSelectionAndReplayPreserveSinglePrimaryWithoutTouchingNeighbour()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateSavedAsync("site-a", "A");
        var second = await fixture.CreateSavedAsync("site-a", "Other");
        var neighbour = await fixture.CreateSavedAsync("site-b", "B");
        fixture.Executor.Devices = [new("same-SN", null, "inverter", "Inverter", "verified-account")];
        IntegrationBindingDto? chosen = null;
        foreach (var (installation, instance) in new[] { ("site-b", neighbour), ("site-a", first), ("site-a", second) })
        {
            var draft = fixture.Change(instance, null);
            var discovery = await fixture.Service(installation).DiscoverAsync(instance.Id, draft, fixture.Actor(installation), default);
            var request = new SelectIntegrationDeviceRequest(draft, discovery.Devices.Single().SelectionToken);
            chosen = await fixture.Service(installation).SelectDeviceAsync(instance.Id, request, fixture.Actor(installation), default);
            Assert.Equal(chosen.Id, (await fixture.Service(installation).SelectDeviceAsync(instance.Id, request, fixture.Actor(installation), default)).Id);
        }
        await using var check = fixture.Factory("site-a").CreateDbContext();
        Assert.Equal(chosen!.Id, (await check.IntegrationDeviceBindings.SingleAsync(b => b.IsDefault)).Id);
        Assert.Equal(neighbour.Id, (await check.IntegrationDeviceBindings.IgnoreQueryFilters().SingleAsync(b => b.InstallationId == "site-b" && b.IsDefault)).InstanceId);
        Assert.False((await check.IntegrationDeviceBindings.SingleAsync(b => b.InstanceId == first.Id)).IsDefault);
    }

    private sealed class Fixture(DbContextOptions<DeyeSolarDbContext> options) : IAsyncDisposable
    {
        private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
        public Executor Executor { get; } = new();
        public IntegrationSecretStore Secrets { get; } = new(new EphemeralDataProtectionProvider());
        private readonly IntegrationChangeNotifier _changes = new(NullLogger<IntegrationChangeNotifier>.Instance);
        private readonly IntegrationSetupGate _gate = new();
        public ServiceProvider ComponentServices(IIntegrationProviderCatalog catalog)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMudServices();
            services.AddSingleton<IJSRuntime, NoJs>();
            services.AddSingleton<NavigationManager, ComponentNavigation>();
            services.AddSingleton<AuthenticationStateProvider>(new ComponentAuthentication(Actor("site-a")));
            var current = new CurrentInstallation(); current.BindOnce("site-a");
            services.AddSingleton(current);
            services.AddSingleton(catalog);
            services.AddSingleton(_changes);
            services.AddSingleton(Service("site-a"));
            return services.BuildServiceProvider();
        }
        public async Task<WebApplication> StartHttpAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddScoped(_ => new DeyeSolarDbContext(options));
            builder.Services.AddIdentity<IdentityUser, IdentityRole>().AddEntityFrameworkStores<DeyeSolarDbContext>();
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MobileBearerAuthenticationHandler>(MobileBearerAuthenticationHandler.SchemeName, _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddAntiforgery();
            builder.Services.AddSingleton(_protection);
            builder.Services.AddSingleton(Secrets);
            builder.Services.AddSingleton(_changes);
            builder.Services.AddSingleton(_gate);
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddScoped<CurrentInstallation>();
            builder.Services.AddScoped<IDbContextFactory<DeyeSolarDbContext>>(provider => Factory(provider.GetRequiredService<CurrentInstallation>().Id ?? "site-a"));
            builder.Services.AddScoped<InstallationMembershipService>();
            builder.Services.AddScoped<IntegrationSetupService>();
            builder.Services.AddSingleton<IIntegrationProviderCatalog>(new Catalog());
            builder.Services.AddSingleton<IIntegrationSetupExecutor>(Executor);
            builder.Services.AddSingleton<IIntegrationPackageManager>(new DeniedPackageManager());
            builder.Services.AddSingleton<LegacyIntegrationBootstrap>();
            builder.Services.AddSingleton<MobileSessionStore>();
            builder.Services.AddSingleton(provider => new TenantRuntimeFactory(options, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                NullLoggerFactory.Instance, TimeProvider.System, provider.GetRequiredService<IHostApplicationLifetime>(), new TenantTestExecutor(), Secrets, _changes));
            builder.Services.AddSingleton<TenantRuntimeRegistry>();
            builder.Services.AddScoped(provider => provider.GetRequiredService<TenantRuntimeRegistry>()
                .Resolve<DynamicSocketGateway>(provider.GetRequiredService<CurrentInstallation>().Id!));
            var app = builder.Build();
            app.UseAuthentication();
            app.UseMiddleware<InstallationBindingMiddleware>();
            app.UseAuthorization();
            app.MapDynamicIntegrations();
            app.MapPost("/_fixture/login", async (HttpLogin request, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn,
                MobileSessionStore sessions, InstallationMembershipService memberships) =>
            {
                var user = await users.FindByIdAsync(request.User);
                if (user is null || !await users.CheckPasswordAsync(user, request.Password)) return Results.Unauthorized();
                var membership = (await memberships.GetForUserAsync(user.Id))!;
                if (request.Cookie) await signIn.SignInWithClaimsAsync(user, false, [new Claim(InstallationIds.ClaimType, membership.InstallationId)]);
                return Results.Ok(new { token = sessions.Create(user.Id, user.UserName!, user.SecurityStamp, membership.InstallationId).Token });
            });
            app.MapGet("/_fixture/csrf", (HttpContext context, IAntiforgery antiforgery) => Results.Ok(new { requestToken = antiforgery.GetAndStoreTokens(context).RequestToken }))
                .RequireAuthorization(ApiAuthorization.AuthenticatedUser);
            using (var scope = app.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                foreach (var id in new[] { "owner-a", "owner-b" })
                {
                    var user = (await users.FindByIdAsync(id))!;
                    Assert.True((await users.UpdateAsync(user)).Succeeded);
                    Assert.True((await users.AddPasswordAsync(user, "LocalDynamic!42")).Succeeded);
                }
            }
            await app.StartAsync();
            return app;
        }
        public Factory Factory(string installation) => new(options, installation);
        public ClaimsPrincipal Actor(string installation) => new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, installation == "site-a" ? "owner-a" : "owner-b"), new Claim(InstallationIds.ClaimType, installation)], "synthetic-test"));
        public IntegrationSetupService Service(string installation, IntegrationRuntimeOptions? setupLimits = null)
        {
            var current = new CurrentInstallation(); current.BindOnce(installation);
            var factory = Factory(installation);
            return new(factory, new Catalog(), Executor, Secrets, _protection, TimeProvider.System, new(factory), current, _changes, _gate,
                setupLimits is null ? null : Options.Create(setupLimits));
        }
        public IntegrationConfigurationChange Change(IntegrationInstanceDto instance, string? key) => new(instance.Revision,
            instance.PackageVersion, instance.PackageDigest, instance.DescriptorDigest, new() { ["region"] = IntegrationJson.Element("eu"), ["extra"] = IntegrationJson.Element(7) },
            key is null ? new() : new() { ["apiKey"] = new("replace", key) });
        public async Task<IntegrationInstanceDto> CreateSavedAsync(string installation, string name)
        {
            var created = await Service(installation).CreateAsync(new("fixture.vendor", name), Actor(installation), default);
            return (await Service(installation).SaveAsync(created.Id, Change(created, "saved-" + name), Actor(installation), default)).Instance;
        }
        public async Task<string> SavedSecretAsync(string installation, Guid id)
        {
            await using var db = Factory(installation).CreateDbContext();
            var instance = await db.IntegrationInstances.SingleAsync(i => i.Id == id);
            var row = await db.IntegrationConfigurations.SingleAsync(c => c.InstanceId == id && c.Revision == instance.Revision);
            return Secrets.Decrypt(installation, id, row.Revision, row.SecretsCiphertext)["apiKey"];
        }
        public async Task<string> StateAsync()
        {
            await using var db = Factory("site-a").CreateDbContext();
            return JsonSerializer.Serialize(new
            {
                instances = await db.IntegrationInstances.IgnoreQueryFilters().OrderBy(i => i.Id).ToListAsync(),
                configurations = await db.IntegrationConfigurations.IgnoreQueryFilters().OrderBy(c => c.InstanceId).ThenBy(c => c.Revision).ToListAsync(),
                devices = await db.IntegrationDeviceBindings.IgnoreQueryFilters().OrderBy(b => b.Id).ToListAsync(),
                rules = await db.TriggerRules.IgnoreQueryFilters().OrderBy(r => r.Id).ToListAsync()
            });
        }
        public async Task<Guid> AddInverterAsync(string installation, Guid instance, bool battery, bool solar)
        {
            await using var db = Factory(installation).CreateDbContext();
            (await db.IntegrationInstances.SingleAsync(i => i.Id == instance)).State = "enabled";
            var device = new IntegrationDeviceBindingEntity
            {
                Id = Guid.NewGuid(),
                InstanceId = instance,
                Kind = "inverter",
                RemoteId = Guid.NewGuid().ToString("D"),
                Name = "Test source",
                Enabled = true,
                MetadataJson = IntegrationJson.Element(new { capabilities = new { hasBattery = battery, hasSolarPower = solar } }).GetRawText()
            };
            db.IntegrationDeviceBindings.Add(device);
            await db.SaveChangesAsync();
            return device.Id;
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION")) { InitialCatalog = "DynamicIntegrationTests_" + Guid.NewGuid().ToString("N") };
            var fixture = new Fixture(new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString).Options);
            await using var db = fixture.Factory("site-a").CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            db.Installations.AddRange(new Installation { Id = "site-a", CreatedAt = DateTimeOffset.UtcNow }, new Installation { Id = "site-b", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.AddRange(new IdentityUser { Id = "owner-a", UserName = "owner-a" }, new IdentityUser { Id = "owner-b", UserName = "owner-b" });
            db.InstallationMemberships.AddRange(new InstallationMembership { UserId = "owner-a", InstallationId = "site-a", Role = "Owner" }, new InstallationMembership { UserId = "owner-b", InstallationId = "site-b", Role = "Owner" });
            await db.SaveChangesAsync();
            return fixture;
        }
        public async ValueTask DisposeAsync() { await using var db = Factory("site-a").CreateDbContext(); await db.Database.EnsureDeletedAsync(); }
    }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options, string installation) : IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext() => new(options, installation);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }
    private sealed class Catalog : IIntegrationProviderCatalog
    {
        public Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationProviderDescriptor>>([Descriptor]);
        public Task<IntegrationProviderDescriptor> GetAsync(string providerId, string? version, CancellationToken ct)
        {
            if (providerId != Descriptor.ProviderId) throw new KeyNotFoundException();
            return Task.FromResult(version switch
            {
                null or "1.0" => Descriptor,
                "1.1" => Descriptor with
                {
                    PackageVersion = "1.1",
                    PackageDigest = "package-1.1",
                    DescriptorDigest = "ui-1.1",
                    Fields = [.. Descriptor.Fields, new("addedSetting", "text", "Additional provider setting", true, IntegrationJson.Element("default-extra"))]
                },
                "2.0" => Descriptor with { PackageVersion = "2.0", PackageDigest = "package-2.0", DescriptorDigest = "ui-2.0", ConfigurationVersion = 2 },
                _ => throw new KeyNotFoundException()
            });
        }
        public async Task<IReadOnlyList<IntegrationProviderDescriptor>> GetVersionsAsync(string providerId, CancellationToken ct)
            => [await GetAsync(providerId, "1.0", ct), await GetAsync(providerId, "1.1", ct), await GetAsync(providerId, "2.0", ct)];
        private static IntegrationProviderDescriptor Descriptor => new("fixture.vendor", "1.0", "package-sha", "descriptor-sha", "Unknown fixture manufacturer", 1, 1,
            ["text", "secret", "select", "integer"], [new("region", "select", "Region", true, Options: [new("eu", "Europe")]),
                new("apiKey", "secret", "API key", true, Secret: true), new("extra", "integer", "Extra setting", true, Minimum: 1, Maximum: 10)], ["test", "discover"]);
    }
    private sealed class DeniedPackageManager : IIntegrationPackageManager
    {
        public Task<IntegrationInstalledPackage> InstallAsync(IntegrationPackageInstallRequest request, CancellationToken ct) => throw new InvalidOperationException("An installation owner must never invoke package installation.");
        public Task<IntegrationInstalledPackage> ResolveAsync(ProviderPackageIdentity identity, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class MissingDescriptorCatalog : IIntegrationProviderCatalog
    {
        public Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationProviderDescriptor>>([]);
        public Task<IntegrationProviderDescriptor> GetAsync(string providerId, string? packageVersion, CancellationToken ct) => throw new KeyNotFoundException();
    }
    private sealed class ThrowingCatalog : IIntegrationProviderCatalog
    {
        public Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct) => throw new InvalidDataException("An installed package was modified.");
        public Task<IntegrationProviderDescriptor> GetAsync(string providerId, string? packageVersion, CancellationToken ct) => throw new InvalidDataException("An installed package was modified.");
    }
    private sealed class ComponentAuthentication(ClaimsPrincipal actor) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(actor));
    }
    private sealed class ComponentNavigation : NavigationManager
    {
        public ComponentNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
    private sealed class SetupRenderer(IServiceProvider services, ILoggerFactory logger) : Renderer(services, logger)
    {
        private IComponent? _component;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        public async Task<int> MountAsync()
        {
            var host = (SetupHost)InstantiateComponent(typeof(SetupHost));
            var root = AssignRootComponentId(host);
            await RenderRootComponentAsync(root);
            _component = host.Form;
            return root;
        }
        public void SetCredential(string key, string value)
        {
            _component!.GetType().GetMethod("SetSecretValue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(_component, [key, value]);
            Repaint();
        }
        public string CredentialValue(string key) => (string)_component!.GetType()
            .GetMethod("SecretValue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(_component, [key])!;
        public void SetVersion(string version)
        {
            var form = _component!.GetType().GetProperty("Current", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_component)!;
            form.GetType().GetProperty("TargetPackageVersion")!.SetValue(form, version);
            Repaint();
        }
        private void Repaint() => typeof(ComponentBase).GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(_component, []);
        public Task ClickAsync(int root, string label) => DispatchEventAsync(FindButton(root, label) ?? throw new InvalidOperationException("Button was not rendered: " + label), null, new MouseEventArgs());
        private ulong? FindButton(int componentId, string label)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType == RenderTreeFrameType.Component && FindButton(frame.ComponentId, label) is { } nested) return nested;
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                var subtree = frames.Array.Skip(index + 1).Take(frame.ElementSubtreeLength - 1).ToArray();
                if (!string.Equals(Text(subtree).Trim(), label, StringComparison.Ordinal)) continue;
                Assert.DoesNotContain(subtree, item => item.FrameType == RenderTreeFrameType.Attribute && item.AttributeName == "disabled" && item.AttributeValue is true);
                return subtree.Single(item => item.FrameType == RenderTreeFrameType.Attribute && item.AttributeName == "onclick").AttributeEventHandlerId;
            }
            return null;
        }
        public string Text(int root)
        {
            var frames = GetCurrentRenderTreeFrames(root);
            return Text(frames.Array.Take(frames.Count));
        }
        private string Text(IEnumerable<RenderTreeFrame> frames) => string.Concat(frames.Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            RenderTreeFrameType.Component => Text(frame.ComponentId),
            _ => ""
        }));
    }
    private sealed class SetupHost : ComponentBase
    {
        public IntegrationSettings? Form { get; private set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<IntegrationSettings>(1);
            builder.AddComponentReferenceCapture(2, value => Form = (IntegrationSettings)value);
            builder.CloseComponent();
        }
    }
    private sealed class Executor : IIntegrationSetupExecutor
    {
        public int Calls { get; private set; }
        public string TestMessage { get; set; } = "Connected.";
        public TaskCompletionSource? TestStarted { get; set; }
        public Func<CancellationToken, Task<IntegrationTestResult>>? Check { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<IntegrationDiscoveredDevice>>>? Discovery { get; set; }
        public IReadOnlyList<IntegrationDiscoveredDevice> Devices { get; set; } = [new("OpaqueCase", "0", "socket", "Socket", "verified-account")];
        public Task<IntegrationTestResult> TestAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, CancellationToken ct)
        { Calls++; TestStarted?.TrySetResult(); return Check?.Invoke(ct) ?? Task.FromResult(new IntegrationTestResult(true, "ok", TestMessage, "verified-account")); }
        public Task<IReadOnlyList<IntegrationDiscoveredDevice>> DiscoverAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, IntegrationDiscoveryQuery query, CancellationToken ct)
        { Calls++; return Discovery?.Invoke(ct) ?? Task.FromResult(Devices); }
    }
}

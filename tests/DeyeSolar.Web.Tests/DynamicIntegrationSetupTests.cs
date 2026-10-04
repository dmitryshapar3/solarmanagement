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
    public Task ARevokedWebCookieStampCannotReadAFreshReadyAuthorization() => AssertRevokedWebStampAsync("status");
    [SqlServerFact]
    public Task ARevokedWebCookieStampCannotUseAFreshReadyAuthorization() => AssertRevokedWebStampAsync("test");
    [SqlServerFact]
    public Task ARevokedWebCookieStampCannotSaveAFreshReadyAuthorization() => AssertRevokedWebStampAsync("save");
    [SqlServerFact]
    public Task ARevokedWebCookieStampCannotCancelAFreshReadyAuthorization() => AssertRevokedWebStampAsync("cancel");

    private async Task AssertRevokedWebStampAsync(string operation)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.Factory("site-a").CreateDbContext())
        {
            (await db.Users.SingleAsync(user => user.Id == "owner-a")).SecurityStamp = "fresh-web-stamp";
            await db.SaveChangesAsync();
        }
        ClaimsPrincipal Stamped(string stamp) => new(new ClaimsIdentity([.. fixture.Actor("site-a").Claims,
            new Claim("AspNet.Identity.SecurityStamp", stamp)], "stamped-cookie-fixture"));
        var fresh = Stamped("fresh-web-stamp");
        var revoked = Stamped("revoked-web-stamp");
        var catalog = new OAuthCatalog();
        var service = fixture.Service("site-a", catalog: catalog);
        var instance = await service.CreateAsync(new("oauth.fixture", "A"), fresh, default);
        var draft = new IntegrationConfigurationChange(instance.Revision, instance.PackageVersion, instance.PackageDigest, instance.DescriptorDigest,
            new() { ["accountLabel"] = IntegrationJson.Element("Draft account") }, new());
        var started = await service.StartAuthorizationAsync(instance.Id, new(draft), fresh, default);
        await fixture.OAuth(catalog).CallbackAsync(fixture.Executor.OAuthBegin!.State, "approved", null, fresh, default);
        var status = await service.AuthorizationStatusAsync(instance.Id, started.FlowId, fresh, default);
        Assert.Equal("ready", status.Status);
        var ready = draft with { Values = new(status.Values), OAuthFlowId = started.FlowId };
        await fixture.CreateSavedAsync("site-b", "B");
        var before = await fixture.StateAsync();
        var calls = fixture.Executor.Calls;
        if (operation == "status")
        {
            var denied = await service.AuthorizationStatusAsync(instance.Id, started.FlowId, revoked, default);
            Assert.Equal("failed", denied.Status);
            Assert.Empty(denied.Values);
            Assert.Empty(denied.SecretPresent);
        }
        else
        {
            await Assert.ThrowsAsync<IntegrationRequestException>(async () =>
            {
                if (operation == "test") await service.TestAsync(instance.Id, ready, revoked, default);
                else if (operation == "save") await service.SaveAsync(instance.Id, ready, revoked, default);
                else await service.CancelAuthorizationAsync(instance.Id, started.FlowId, revoked, default);
            });
        }
        Assert.Equal(calls, fixture.Executor.Calls);
        Assert.Equal(before, await fixture.StateAsync());
        await using var persisted = fixture.Factory("site-a").CreateDbContext();
        var flow = await persisted.IntegrationOAuthFlows.SingleAsync(flow => flow.Id == started.FlowId);
        Assert.Equal("ready", flow.Status);
        Assert.NotEmpty(flow.Ciphertext);
        Assert.Equal("ready", (await service.AuthorizationStatusAsync(instance.Id, started.FlowId, fresh, default)).Status);
    }
    [SqlServerFact]
    public async Task WebOAuthKeepsTokensServerSideAndAppliesTheReadyDraftOnlyOnExplicitSave()
    {
        await using var fixture = await Fixture.CreateAsync();
        var catalog = new OAuthCatalog();
        var service = fixture.Service("site-a", catalog: catalog);
        var instance = await service.CreateAsync(new("oauth.fixture", "OAuth connection"), fixture.Actor("site-a"), default);
        var neighbour = await fixture.CreateSavedAsync("site-b", "B");
        var before = await fixture.StateAsync();
        await using var services = fixture.ComponentServices(catalog);
        await using var renderer = new SetupRenderer(services, NullLoggerFactory.Instance);
        Guid flowId = default;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            await renderer.ClickAsync(root, "Authorize provider account");
            Assert.Contains("Open provider authorization", renderer.Text(root));
            Assert.Equal("S256", fixture.Executor.OAuthBegin!.CodeChallengeMethod);
            Assert.DoesNotContain("oauth-web-secret", renderer.Attributes(root));
            Assert.Equal(before, await fixture.StateAsync());
            await fixture.OAuth(catalog).CallbackAsync(fixture.Executor.OAuthBegin.State, "local-fixture-code", null, fixture.Actor("site-a"), default);
            await renderer.ClickAsync(root, "Check authorization");
            Assert.Contains("Authorization status: ready", renderer.Text(root));
            Assert.Equal("Authorized account", renderer.FieldValue("accountLabel"));
            Assert.DoesNotContain("oauth-web-secret", renderer.Attributes(root));
            Assert.DoesNotContain("oauth-web-secret", renderer.Text(root));
            flowId = renderer.Draft().OAuthFlowId!.Value;
            await renderer.ClickAsync(root, "Test connection");
            await renderer.ClickAsync(root, "Find devices");
            Assert.Equal(before, await fixture.StateAsync());
            Assert.Equal("oauth-web-secret", fixture.Executor.LastDraft!.Secrets["apiKey"]);
            await renderer.ClickAsync(root, "Save settings");
            Assert.Contains("Settings saved", renderer.Text(root));
            Assert.Null(renderer.Draft().OAuthFlowId);
        });
        var saved = await service.ReadAsync(instance.Id, default);
        Assert.Equal(instance.Revision + 1, saved.Instance.Revision);
        Assert.Equal("Authorized account", saved.Values["accountLabel"].GetString());
        Assert.Equal("oauth-web-secret", await fixture.SavedSecretAsync("site-a", instance.Id));
        await using var db = fixture.Factory("site-a").CreateDbContext();
        var flow = await db.IntegrationOAuthFlows.SingleAsync(f => f.Id == flowId);
        Assert.Equal("consumed", flow.Status);
        Assert.Equal("", flow.Ciphertext);
        Assert.Empty(await service.DevicesAsync(instance.Id, default));
        Assert.Equal(neighbour, (await fixture.Service("site-b").ReadAsync(neighbour.Id, default)).Instance);
        Assert.Equal("saved-B", await fixture.SavedSecretAsync("site-b", neighbour.Id));
    }

    [SqlServerFact]
    public async Task WebOAuthDraftEditInvalidatesTheResultAndExplicitCancellationPreservesSavedSettings()
    {
        await using var fixture = await Fixture.CreateAsync();
        var catalog = new OAuthCatalog();
        var service = fixture.Service("site-a", catalog: catalog);
        var instance = await service.CreateAsync(new("oauth.fixture", "A"), fixture.Actor("site-a"), default);
        await fixture.CreateSavedAsync("site-b", "B");
        var before = await fixture.StateAsync();
        await using var services = fixture.ComponentServices(catalog);
        await using var renderer = new SetupRenderer(services, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            await renderer.ClickAsync(root, "Authorize provider account");
            await fixture.OAuth(catalog).CallbackAsync(fixture.Executor.OAuthBegin!.State, "fixture-code", null, fixture.Actor("site-a"), default);
            await renderer.ClickAsync(root, "Check authorization");
            Assert.NotNull(renderer.Draft().OAuthFlowId);
            renderer.SetText("accountLabel", "Edited after authorization");
            Assert.Null(renderer.Draft().OAuthFlowId);
            Assert.Contains("The draft changed", renderer.Text(root));
            var calls = fixture.Executor.Calls;
            await renderer.ClickAsync(root, "Test connection");
            Assert.Equal(calls, fixture.Executor.Calls);
            Assert.Contains("Enter API key", renderer.Text(root));
            await renderer.ClickAsync(root, "Authorize provider account");
            Assert.Equal("Edited after authorization", renderer.FieldValue("accountLabel"));
            await renderer.ClickAsync(root, "Cancel authorization");
            Assert.Contains("Authorization cancelled", renderer.Text(root));
            Assert.Null(renderer.Draft().OAuthFlowId);
            Assert.DoesNotContain("oauth-web-secret", renderer.Attributes(root));
        });
        Assert.Equal(before, await fixture.StateAsync());
        Assert.False((await service.ReadAsync(instance.Id, default)).SecretPresent.ContainsKey("apiKey"));
        await using var db = fixture.Factory("site-a").CreateDbContext();
        var flows = await db.IntegrationOAuthFlows.Where(f => f.InstanceId == instance.Id).ToArrayAsync();
        Assert.Equal(2, flows.Length);
        Assert.All(flows, flow => { Assert.Equal("cancelled", flow.Status); Assert.Equal("", flow.Ciphertext); });
    }
    [SqlServerFact]
    public async Task WizardRendersPlainInstructionsNavigatesAndRetainsHiddenDraftValuesWithoutSavingDuringDiscovery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var catalog = new WizardCatalog();
        var service = fixture.Service("site-a", catalog: catalog);
        var created = await service.CreateAsync(new("wizard.fixture", "Wizard connection"), fixture.Actor("site-a"), default);
        var saved = (await service.SaveAsync(created.Id, WizardChange(created, "basic", null, "saved-wizard-key"), fixture.Actor("site-a"), default)).Instance;
        await fixture.CreateSavedAsync("site-b", "Neighbour");
        var before = await fixture.StateAsync();
        await using var services = fixture.ComponentServices(catalog);
        await using var renderer = new SetupRenderer(services, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            Assert.Contains("Step 1 of 2 · Credentials", renderer.Text(root));
            Assert.Contains("<script>Never execute this instruction</script>", renderer.Text(root));
            Assert.DoesNotContain("<script>", renderer.Markup(root));
            Assert.DoesNotContain("Advanced threshold", renderer.Text(root));
            renderer.SetText("mode", "advanced");
            await renderer.ClickAsync(root, "Next step");
            Assert.Contains("Step 2 of 2 · Devices", renderer.Text(root));
            Assert.Contains("Advanced threshold", renderer.Text(root));
            await renderer.ClickAsync(root, "Save settings");
            Assert.Contains("Enter Advanced threshold", renderer.Text(root));
            renderer.SetText("limit", "7");
            await renderer.ClickAsync(root, "Previous step");
            renderer.SetText("mode", "basic");
            await renderer.ClickAsync(root, "Next step");
            Assert.DoesNotContain("Advanced threshold", renderer.Text(root));
            Assert.Equal("7", renderer.FieldValue("limit"));
            await renderer.ClickAsync(root, "Find devices");
            Assert.Contains("Socket", renderer.Text(root));
            Assert.Equal(before, await fixture.StateAsync());
            await renderer.ClickAsync(root, "Save settings");
            Assert.Contains("Settings saved", renderer.Text(root));
        });
        var view = await service.ReadAsync(saved.Id, default);
        Assert.Equal("basic", view.Values["mode"].GetString());
        Assert.Equal(7, view.Values["limit"].GetInt32());
        Assert.Equal("saved-wizard-key", await fixture.SavedSecretAsync("site-a", saved.Id));
        Assert.Equal("saved-Neighbour", await fixture.SavedSecretAsync("site-b", (await fixture.Service("site-b").ListAsync(default)).Single().Id));
        Assert.Empty(await service.DevicesAsync(saved.Id, default));
    }

    [SqlServerFact]
    public async Task ConditionalRequiredFieldsAreAuthoritativeAndHiddenOmittedValuesAreRetainedAndStillTypeChecked()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service("site-a", catalog: new WizardCatalog());
        var instance = await service.CreateAsync(new("wizard.fixture", "A"), fixture.Actor("site-a"), default);
        var inactive = await service.SaveAsync(instance.Id, WizardChange(instance, "basic", null, "conditional-key"), fixture.Actor("site-a"), default);
        await fixture.CreateSavedAsync("site-b", "B");
        var before = await fixture.StateAsync();
        var badHidden = WizardChange(inactive.Instance, "basic", null, null);
        badHidden.Values["limit"] = IntegrationJson.Element("seven");
        await Assert.ThrowsAsync<IntegrationRequestException>(() => service.SaveAsync(instance.Id, badHidden, fixture.Actor("site-a"), default));
        await Assert.ThrowsAsync<IntegrationRequestException>(() => service.TestAsync(instance.Id,
            WizardChange(inactive.Instance, "advanced", null, null), fixture.Actor("site-a"), default));
        Assert.Equal(before, await fixture.StateAsync());
        var active = await service.SaveAsync(instance.Id, WizardChange(inactive.Instance, "advanced", 8, null), fixture.Actor("site-a"), default);
        var hide = WizardChange(active.Instance, "basic", null, null);
        hide.SecretOperations["apiKey"] = new("clear");
        var hidden = await service.SaveAsync(instance.Id, hide, fixture.Actor("site-a"), default);
        Assert.Equal(8, hidden.Values["limit"].GetInt32());
        Assert.False(hidden.SecretPresent.ContainsKey("apiKey"));
        var unchanged = await fixture.StateAsync();
        await Assert.ThrowsAsync<IntegrationRequestException>(() => service.SaveAsync(instance.Id,
            WizardChange(hidden.Instance, "advanced", 8, null), fixture.Actor("site-a"), default));
        Assert.Equal(unchanged, await fixture.StateAsync());
        Assert.Equal("saved-B", await fixture.SavedSecretAsync("site-b", (await fixture.Service("site-b").ListAsync(default)).Single().Id));
    }

    private static IntegrationConfigurationChange WizardChange(IntegrationInstanceDto instance, string mode, int? limit, string? secret)
    {
        var values = new Dictionary<string, JsonElement> { ["mode"] = IntegrationJson.Element(mode) };
        if (limit is not null) values["limit"] = IntegrationJson.Element(limit.Value);
        return new(instance.Revision, instance.PackageVersion, instance.PackageDigest, instance.DescriptorDigest, values,
            secret is null ? new() : new() { ["apiKey"] = new("replace", secret) });
    }

    [SqlServerFact]
    public async Task WebWizardPreservesExplicitNullInsteadOfApplyingADefaultOrActivatingAConditionalGroup()
    {
        await using var fixture = await Fixture.CreateAsync();
        var catalog = new NullWizardCatalog();
        var service = fixture.Service("site-a", catalog: catalog);
        var instance = await service.CreateAsync(new("null.fixture", "A"), fixture.Actor("site-a"), default);
        var saved = await service.SaveAsync(instance.Id, new(instance.Revision, instance.PackageVersion, instance.PackageDigest, instance.DescriptorDigest,
            new() { ["enabled"] = IntegrationJson.Element<bool?>(null), ["threshold"] = IntegrationJson.Element<int?>(null) }, new()), fixture.Actor("site-a"), default);
        await fixture.CreateSavedAsync("site-b", "B");
        var before = await fixture.StateAsync();
        await using var services = fixture.ComponentServices(catalog);
        await using var renderer = new SetupRenderer(services, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            Assert.DoesNotContain("Defaulted advanced group", renderer.Text(root));
            Assert.DoesNotContain("Conditional threshold", renderer.Text(root));
            Assert.Equal(JsonValueKind.Null, renderer.Draft().Values["enabled"].ValueKind);
            Assert.Equal(JsonValueKind.Null, renderer.Draft().Values["threshold"].ValueKind);
            await renderer.ClickAsync(root, "Test connection");
            Assert.Equal(JsonValueKind.Null, fixture.Executor.LastDraft!.Values.GetProperty("enabled").ValueKind);
            await renderer.ClickAsync(root, "Save settings");
        });
        Assert.Equal(before, await fixture.StateAsync());
        Assert.Equal(saved.Instance.Revision, (await service.ReadAsync(instance.Id, default)).Instance.Revision);
    }

    [SqlServerFact]
    public async Task RequiredBooleanHasAnUnsetStateAndFalseCanBeChosenDirectlyWithoutActivatingItsConditionalGroup()
    {
        await using var fixture = await Fixture.CreateAsync();
        var catalog = new RequiredBooleanCatalog();
        var service = fixture.Service("site-a", catalog: catalog);
        var instance = await service.CreateAsync(new("boolean.fixture", "A"), fixture.Actor("site-a"), default);
        await fixture.CreateSavedAsync("site-b", "B");
        var before = await fixture.StateAsync();
        await using var services = fixture.ComponentServices(catalog);
        await using var renderer = new SetupRenderer(services, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            await renderer.ClickAsync(root, "Next step");
            Assert.Contains("Enter Enabled flag", renderer.Text(root));
            Assert.Contains("Step 1 of 2", renderer.Text(root));
            Assert.Equal(before, await fixture.StateAsync());
            await renderer.SelectFieldAsync(root, "enabled", "false");
            Assert.Equal(JsonValueKind.False, renderer.Draft().Values["enabled"].ValueKind);
            await renderer.ClickAsync(root, "Next step");
            Assert.Contains("Step 2 of 2", renderer.Text(root));
            Assert.DoesNotContain("Conditional threshold", renderer.Text(root));
            await renderer.ClickAsync(root, "Save settings");
        });
        var saved = await service.ReadAsync(instance.Id, default);
        Assert.Equal(JsonValueKind.False, saved.Values["enabled"].ValueKind);
        Assert.False(saved.Values.ContainsKey("threshold"));
        Assert.Equal("saved-B", await fixture.SavedSecretAsync("site-b", (await fixture.Service("site-b").ListAsync(default)).Single().Id));
    }
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
        services.AddComponentLocalization();
            services.AddMudServices();
            services.AddSingleton<IJSRuntime, NoJs>();
            services.AddSingleton<NavigationManager, ComponentNavigation>();
            services.AddSingleton<AuthenticationStateProvider>(new ComponentAuthentication(Actor("site-a")));
            var current = new CurrentInstallation(); current.BindOnce("site-a");
            services.AddSingleton(current);
            services.AddSingleton(catalog);
            services.AddSingleton(_changes);
            services.AddSingleton(Service("site-a", catalog: catalog));
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
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(new IntegrationOAuthOptions());
            builder.Services.AddSingleton<IOptions<IntegrationRuntimeOptions>>(Options.Create(new IntegrationRuntimeOptions()));
            builder.Services.AddSingleton<IntegrationOAuthService>();
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
        public IntegrationSetupService Service(string installation, IntegrationRuntimeOptions? setupLimits = null, IIntegrationProviderCatalog? catalog = null)
        {
            var current = new CurrentInstallation(); current.BindOnce(installation);
            var factory = Factory(installation);
            return new(factory, catalog ?? new Catalog(), Executor, Secrets, _protection, TimeProvider.System, new(factory), current, _changes, _gate,
                setupLimits is null ? null : Options.Create(setupLimits), OAuth(catalog ?? new Catalog()));
        }
        public IntegrationOAuthService OAuth(IIntegrationProviderCatalog catalog) => new(options, catalog, Executor, Secrets, new(), TimeProvider.System,
            new MobileSessionStore(), _gate, Options.Create(new IntegrationRuntimeOptions()));
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
    private sealed class WizardCatalog : IIntegrationProviderCatalog
    {
        private static IntegrationProviderDescriptor Descriptor => new("wizard.fixture", "1.0", "wizard-package", "wizard-ui", "Wizard manufacturer", 1, 1,
            ["select", "secret", "integer", "wizard", "groups", "instructions", "conditional-fields", "device-selector"],
            [new("mode", "select", "Mode", true, Options: [new("basic", "Basic"), new("advanced", "Advanced")]),
                new("apiKey", "secret", "API key", true, Secret: true, ActiveWhen: new("mode", "eq", IntegrationJson.Element("advanced"))),
                new("limit", "integer", "Advanced threshold", true, Minimum: 1, Maximum: 10)], ["test", "discover"],
            new(1, [new("credentials", "Credentials", "<script>Never execute this instruction</script>",
                    [new("account", "Account settings", "Choose the account mode.", ["mode", "apiKey"], ["test"])]),
                new("devices", "Devices", "Choose devices explicitly.",
                    [new("advanced", "Advanced settings", null, ["limit"], [], new("mode", "eq", IntegrationJson.Element("advanced"))),
                        new("inventory", "Device selection", "Discovery does not save settings.", [], ["discover"])])]));
        public Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationProviderDescriptor>>([Descriptor]);
        public Task<IntegrationProviderDescriptor> GetAsync(string providerId, string? version, CancellationToken ct) => Task.FromResult(Descriptor);
    }
    private sealed class OAuthCatalog : IIntegrationProviderCatalog
    {
        private static IntegrationProviderDescriptor Descriptor => new("oauth.fixture", "1.0", "oauth-package", "oauth-ui", "OAuth fixture provider", 1, 1,
            ["text", "secret", "oauth"], [new("accountLabel", "text", "Account label", true, IntegrationJson.Element("Draft account")),
                new("apiKey", "secret", "API key", true, Secret: true)], ["test", "discover", "oauth"], OAuthDefinition: new(["apiKey"]));
        public Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationProviderDescriptor>>([Descriptor]);
        public Task<IntegrationProviderDescriptor> GetAsync(string providerId, string? version, CancellationToken ct) => Task.FromResult(Descriptor);
    }
    private sealed class NullWizardCatalog : IIntegrationProviderCatalog
    {
        private static IntegrationProviderDescriptor Descriptor => new("null.fixture", "1.0", "null-package", "null-ui", "Nullable fixture provider", 1, 1,
            ["boolean", "integer", "wizard", "groups", "conditional-fields"],
            [new("enabled", "boolean", "Advanced enabled", false, IntegrationJson.Element(true)), new("threshold", "integer", "Conditional threshold", true)], ["test"],
            new(1, [new("settings", "Settings", null, [new("controls", "Controls", null, ["enabled"], ["test"]),
                new("advanced", "Defaulted advanced group", null, ["threshold"], [], new("enabled", "eq", IntegrationJson.Element(true)))])]));
        public Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationProviderDescriptor>>([Descriptor]);
        public Task<IntegrationProviderDescriptor> GetAsync(string providerId, string? version, CancellationToken ct) => Task.FromResult(Descriptor);
    }
    private sealed class RequiredBooleanCatalog : IIntegrationProviderCatalog
    {
        private static IntegrationProviderDescriptor Descriptor => new("boolean.fixture", "1.0", "boolean-package", "boolean-ui", "Boolean fixture", 1, 1,
            ["boolean", "integer", "wizard", "groups", "conditional-fields"],
            [new("enabled", "boolean", "Enabled flag", true), new("threshold", "integer", "Conditional threshold", true)], ["test"],
            new(1, [new("controls", "Controls", null, [new("controls", "Controls", null, ["enabled"], [])]),
                new("settings", "Settings", null, [new("advanced", "Advanced settings", null, ["threshold"], ["test"], new("enabled", "eq", IntegrationJson.Element(true)))])]));
        public Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationProviderDescriptor>>([Descriptor]);
        public Task<IntegrationProviderDescriptor> GetAsync(string providerId, string? version, CancellationToken ct) => Task.FromResult(Descriptor);
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
        public void SetText(string key, string value)
        {
            _component!.GetType().GetMethod("SetText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(_component, [key, value]);
            Repaint();
        }
        public string FieldValue(string key) => (string)_component!.GetType().GetMethod("TextValue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(_component, [key])!;
        public IntegrationConfigurationChange Draft() => (IntegrationConfigurationChange)_component!.GetType().GetMethod("Draft", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(_component, [])!;
        public async Task SelectFieldAsync(int root, string key, string value)
        {
            var callback = FindFieldCallback(root, key) ?? throw new InvalidOperationException("The field is not rendered: " + key);
            await callback.InvokeAsync(value);
        }
        private EventCallback<string>? FindFieldCallback(int root, string key)
        {
            var frames = GetCurrentRenderTreeFrames(root);
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Component) continue;
                if (frame.ComponentType == typeof(IntegrationFieldInput))
                {
                    var parameters = frames.Array.Skip(index + 1).Take(frame.ComponentSubtreeLength - 1).ToArray();
                    if (parameters.Any(parameter => parameter.FrameType == RenderTreeFrameType.Attribute
                        && parameter.AttributeName == "Field" && parameter.AttributeValue is IntegrationFieldDescriptor field && field.Key == key))
                        return (EventCallback<string>)parameters.Single(parameter => parameter.FrameType == RenderTreeFrameType.Attribute && parameter.AttributeName == "ValueChanged").AttributeValue;
                }
                if (FindFieldCallback(frame.ComponentId, key) is { } nested) return nested;
            }
            return null;
        }
        public string Attributes(int root)
        {
            var frames = GetCurrentRenderTreeFrames(root);
            return string.Concat(frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
            {
                RenderTreeFrameType.Attribute => frame.AttributeValue?.ToString(),
                RenderTreeFrameType.Component => Attributes(frame.ComponentId),
                _ => ""
            }));
        }
        public string Markup(int root)
        {
            var frames = GetCurrentRenderTreeFrames(root);
            return string.Concat(frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
            {
                RenderTreeFrameType.Markup => frame.MarkupContent,
                RenderTreeFrameType.Component => Markup(frame.ComponentId),
                _ => ""
            }));
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
        public IntegrationDraftConfiguration? LastDraft { get; private set; }
        public IntegrationOAuthBeginRequest? OAuthBegin { get; private set; }
        public Task<IntegrationOAuthBeginResult> BeginAuthorizationAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, IntegrationOAuthBeginRequest request, CancellationToken ct)
        {
            OAuthBegin = request;
            return Task.FromResult(new IntegrationOAuthBeginResult("https://provider.example/authorize?state=" + request.State + "&code_challenge=" + request.CodeChallenge));
        }
        public Task<IntegrationOAuthCompleteResult> CompleteAuthorizationAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, IntegrationOAuthCompleteRequest request, CancellationToken ct)
            => Task.FromResult(new IntegrationOAuthCompleteResult(true, IntegrationJson.Element(new { accountLabel = "Authorized account" }),
                new Dictionary<string, string> { ["apiKey"] = "oauth-web-secret" }, "verified-account"));
        public Task<IntegrationTestResult> TestAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, CancellationToken ct)
        { Calls++; LastDraft = draft; TestStarted?.TrySetResult(); return Check?.Invoke(ct) ?? Task.FromResult(new IntegrationTestResult(true, "ok", TestMessage, "verified-account")); }
        public Task<IReadOnlyList<IntegrationDiscoveredDevice>> DiscoverAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, IntegrationDiscoveryQuery query, CancellationToken ct)
        { Calls++; return Discovery?.Invoke(ct) ?? Task.FromResult(Devices); }
    }
}

using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Redesign;
using DeyeSolar.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Tests;

public class SettingsComponentTests
{
    [SqlServerFact]
    public async Task ReadOnlyAutomationsKeepDetailsAndEvaluationButBlockAllChanges()
    {
        await using var fixture = await Fixture.CreateAsync(false);
        await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await fixture.Renderer.MountAutomationsAsync();
            Assert.Contains("Read-only automation", fixture.Renderer.Text(root));
            Assert.Contains("Right now", fixture.Renderer.Text(root));
            Assert.Contains("Not checked yet", fixture.Renderer.Text(root));
            Assert.True(fixture.Renderer.HasLink(root, "/automations/1"));
            Assert.Equal(1, fixture.Renderer.DisabledFieldsets(root));
            foreach (var label in new[] { "New automation", "Use solar surplus", "Keep a battery reserve", "Daylight only", "Save automation", "Delete" })
                Assert.True(fixture.Renderer.Button(root, label).Disabled);
            Assert.True(fixture.Renderer.InputDisabled(root, "Enable Read-only automation"));
            Assert.True(fixture.Renderer.InputDisabled(root, "Enabled"));
            var initialThreshold = fixture.Renderer.RuleDraft.SocTurnOnThreshold;
            await fixture.Renderer.InvokeAsync("SetOn", 90d);
            await fixture.Renderer.InvokeAsync("SourceChanged", new ChangeEventArgs { Value = Guid.NewGuid().ToString() });
            await fixture.Renderer.InvokeAsync("Template", 0);
            await fixture.Renderer.InvokeAsync("Save");
            await fixture.Renderer.InvokeAsync("Toggle", fixture.Rules.Rule, true);
            await fixture.Renderer.InvokeAsync("Delete");
            Assert.Equal(initialThreshold, fixture.Renderer.RuleDraft.SocTurnOnThreshold);
            Assert.Null(fixture.Renderer.RuleDraft.SourceInverterId);
            Assert.Equal(0, fixture.Rules.Mutations);
            Assert.Equal(1, fixture.Access.RuleChecks);
        });
    }

    [SqlServerTheory]
    [InlineData("Save")]
    [InlineData("Toggle")]
    [InlineData("Delete")]
    public async Task RevokedAutomationPermissionIsRecheckedBeforeEachMutation(string action)
    {
        await using var fixture = await Fixture.CreateAsync(true);
        await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await fixture.Renderer.MountAutomationsAsync();
            fixture.Access.CanManage = false;
            await fixture.Renderer.InvokeAsync(action, action == "Toggle" ? [fixture.Rules.Rule, true] : []);
            fixture.Renderer.Repaint();
            Assert.Equal(2, fixture.Access.RuleChecks);
            Assert.Equal(0, fixture.Rules.Mutations);
            Assert.True(fixture.Renderer.Button(root, "Save automation").Disabled);
            Assert.Contains("Your access has changed", fixture.Renderer.Text(root));
        });
    }

    [SqlServerFact]
    public async Task AutomationShowsTheRecordedDecisionAndDoesNotReevaluateAnUnsavedDraft()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        await fixture.SeedEvaluationAsync(new(1, fixture.Rules.Rule.EntityId, DateTimeOffset.UtcNow, null,
            RuleDecisionReason.SocBelowTurnOnThreshold, 50, 75, 55, 0, false, false, null, 3000));
        await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await fixture.Renderer.MountAutomationsAsync();
            Assert.Contains("Battery is below the switch-on threshold", fixture.Renderer.Text(root));
            Assert.Contains("50 / 75%", fixture.Renderer.Text(root));
            Assert.Contains("These are recorded values for the saved automation. Unsaved changes are not evaluated", fixture.Renderer.Text(root));
            await fixture.Renderer.InvokeAsync("SetOn", 90d);
            fixture.Renderer.Repaint();
            Assert.Contains("50 / 75%", fixture.Renderer.Text(root));
            Assert.DoesNotContain("50 / 90%", fixture.Renderer.Text(root));
            Assert.Equal(0, fixture.Rules.Mutations);
        });
    }

    [SqlServerFact]
    public async Task ReadOnlyInstallationCannotEditSaveOrProbeButKeepsPersonalAppearance()
    {
        await using var fixture = await Fixture.CreateAsync(false);
        await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await fixture.Renderer.MountAsync();
            var saved = fixture.Renderer.Draft;
            Assert.Equal(2, fixture.Renderer.DisabledFieldsets(root));
            Assert.True(fixture.Renderer.Button(root, "Save changes").Disabled);
            Assert.True(fixture.Renderer.Button(root, "Roof 1 · Compass bearing · degrees · E 90°").Disabled);
            Assert.False(fixture.Renderer.AppearanceDisabled(root));
            fixture.Renderer.SetSolar(saved.Site.SolarEstimate with { Latitude = 44, Roof1Azimuth = 90 });
            await fixture.Renderer.InvokeAsync("Save");
            await fixture.Renderer.DispatchAsync(fixture.Renderer.Button(root, "Test weather here").Id);
            Assert.Equal(saved, fixture.Renderer.Draft);
            Assert.Empty(fixture.Probe.Requests);
        });
        await fixture.AssertNoSavedSettingsAsync();
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftWeatherUsesUnsavedCoordinatesAndCannotPublishAfterChangeOrDiscard(bool discard)
    {
        await using var fixture = await Fixture.CreateAsync(true);
        var root = 0;
        Task pending = Task.CompletedTask;
        await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            root = await fixture.Renderer.MountAsync();
            fixture.Renderer.SetSolar(fixture.Renderer.Draft.Site.SolarEstimate with { Latitude = 47.4, Longitude = 12.7 });
            await fixture.Renderer.RenderAsync(root);
            pending = fixture.Renderer.DispatchAsync(fixture.Renderer.Button(root, "Test weather here").Id);
        });
        await fixture.Probe.Started.Task;
        var request = Assert.Single(fixture.Probe.Requests);
        Assert.Equal(new SolarTestSettings(47.4, 12.7), request.SolarEstimate);
        Assert.Equal(2, fixture.Access.ManageChecks); // Initialization plus fresh permission at the probe.
        await fixture.AssertNoSavedSettingsAsync();
        Task draftRender = Task.CompletedTask;
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            if (discard) _ = fixture.Renderer.InvokeAsync("Discard");
            else fixture.Renderer.SetSolar(fixture.Renderer.Draft.Site.SolarEstimate with { Latitude = 48.5 });
            draftRender = fixture.Renderer.RenderAsync(root);
        });
        fixture.Probe.Complete.SetResult(new("openmeteo", true, "ok", "Obsolete draft forecast result", DateTimeOffset.UtcNow));
        await Task.WhenAll(pending, draftRender).WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => Assert.DoesNotContain("Obsolete draft forecast result", fixture.Renderer.Text(root)));
        await fixture.AssertNoSavedSettingsAsync();
    }

    [SqlServerFact]
    public async Task PermissionIsRecheckedBeforeWeatherAndDeniedProbeDoesNotCallProviderOrSave()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await fixture.Renderer.MountAsync();
            fixture.Access.CanManage = false;
            await fixture.Renderer.DispatchAsync(fixture.Renderer.Button(root, "Test weather here").Id);
            Assert.Equal(2, fixture.Access.ManageChecks);
            Assert.Empty(fixture.Probe.Requests);
        });
        await fixture.AssertNoSavedSettingsAsync();
    }

    [SqlServerFact]
    public async Task PermissionRevocationBeforeSaveDoesNotPersistTheDraft()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await fixture.Renderer.MountAsync();
            fixture.Renderer.SetSolar(fixture.Renderer.Draft.Site.SolarEstimate with { LocationLabel = "Unsaved permitted draft" });
            fixture.Access.CanManage = false;
            await fixture.Renderer.InvokeAsync("Save");
            await fixture.Renderer.RenderAsync(root);
            Assert.Equal(2, fixture.Access.ManageChecks);
            Assert.True(fixture.Renderer.Button(root, "Save changes").Disabled);
        });
        await fixture.AssertNoSavedSettingsAsync();
    }

    [SqlServerFact]
    public async Task SuccessfulDraftWeatherCheckPublishesWithoutSavingAndEditingCoordinatesClearsIt()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        fixture.Probe.Complete.SetResult(new("openmeteo", true, "ok", "Draft coordinates verified", DateTimeOffset.UtcNow));
        await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await fixture.Renderer.MountAsync();
            fixture.Renderer.SetSolar(fixture.Renderer.Draft.Site.SolarEstimate with { Latitude = 47.4, Longitude = 12.7 });
            await fixture.Renderer.RenderAsync(root);
            await fixture.Renderer.DispatchAsync(fixture.Renderer.Button(root, "Test weather here").Id);
            Assert.Contains("Draft coordinates verified", fixture.Renderer.Text(root));
            fixture.Renderer.SetSolar(fixture.Renderer.Draft.Site.SolarEstimate with { Longitude = 13.2 });
            await fixture.Renderer.RenderAsync(root);
            Assert.DoesNotContain("Draft coordinates verified", fixture.Renderer.Text(root));
        });
        await fixture.AssertNoSavedSettingsAsync();
    }

    private sealed class Access(bool allowed) : IInstallationAccessAuthorizer
    {
        public bool CanManage { get; set; } = allowed;
        public int ManageChecks { get; private set; }
        public int RuleChecks { get; private set; }
        public Task<InstallationMembership> CheckAsync(ClaimsPrincipal actor, string installationId, InstallationPermission permission, CancellationToken ct = default)
        {
            if (permission == InstallationPermission.ManageSettings)
            {
                ++ManageChecks;
                if (!CanManage) throw new InstallationAccessException("Your access has changed. Sign in again or contact the installation owner.");
            }
            if (permission == InstallationPermission.ManageRules)
            {
                ++RuleChecks;
                if (!CanManage) throw new InstallationAccessException("Your access has changed. Sign in again or contact the installation owner.");
            }
            return Task.FromResult(new InstallationMembership { InstallationId = installationId, UserId = "settings-reader", Role = CanManage ? "Owner" : "Viewer" });
        }
    }
    private sealed class Probe : IIntegrationTestService
    {
        public List<IntegrationTestRequest> Requests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IntegrationTestResult> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IntegrationTestResult> TestAsync(string kind, IntegrationTestRequest request, CancellationToken ct)
        {
            Assert.Equal("openmeteo", kind); Requests.Add(request); Started.TrySetResult(); return Complete.Task;
        }
    }
    private sealed class Fixture(SqlServerTestDatabase database, ServiceProvider services, EventRenderer renderer, Access access, Probe probe, Rules rules) : IAsyncDisposable
    {
        public EventRenderer Renderer => renderer;
        public Access Access => access;
        public Probe Probe => probe;
        public Rules Rules => rules;
        public static async Task<Fixture> CreateAsync(bool allowed)
        {
            var database = await SqlServerTestDatabase.CreateAsync("SettingsComponent", SqlTestSchema.Model, seed: async db =>
            { db.Installations.Add(new Installation { Id = TestInstallation.Id }); await db.SaveChangesAsync(); });
            var access = new Access(allowed); var current = new CurrentInstallation(); current.BindOnce(TestInstallation.Id);
            var security = new InteractiveSecurityContext(access, current, new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
            security.BindOnce(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "settings-reader")], "SettingsTest")));
            var probe = new Probe(); var services = new ServiceCollection();
            services.AddLogging(); services.AddComponentLocalization(); services.AddSingleton<IJSRuntime, NullJs>(); services.AddSingleton<NavigationManager, Navigation>();
            services.AddSingleton(security); services.AddSingleton<IIntegrationTestService>(probe);
            services.AddSingleton<IDbContextFactory<DeyeSolarDbContext>>(database.Factory);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()); services.AddSingleton<AppSettingsService>();
            services.AddSingleton<IntegrationChangeNotifier>(); services.AddSingleton<InstallationSettingsService>();
            var rules = new Rules();
            services.AddSingleton<IConfigurationRules>(rules); services.AddSingleton<ISocketInventoryService, Inventory>();
            services.AddSingleton<IInverterCatalog, Inverters>(); services.AddSingleton(TimeProvider.System);
            services.AddSingleton<DeviceStatusSnapshot>();
            services.AddSingleton<DeviceNameService>(provider => new(new AppSettingsDeviceLabelStore(provider.GetRequiredService<AppSettingsService>(), provider.GetRequiredService<AppSettingsService>()), provider.GetRequiredService<DeviceStatusSnapshot>()));
            services.AddSingleton<RedesignQueries>(provider => new(database.Factory, rules, TimeProvider.System, security, provider.GetRequiredService<DeviceStatusSnapshot>(), provider.GetRequiredService<DeviceNameService>(), null!));
            var provider = services.BuildServiceProvider();
            return new(database, provider, new(provider, provider.GetRequiredService<ILoggerFactory>()), access, probe, rules);
        }
        public async Task AssertNoSavedSettingsAsync() { await using var db = database.Factory.CreateDbContext(); Assert.Empty(await db.AppSettings.ToListAsync()); }
        public async Task SeedEvaluationAsync(RuleDecision decision) { await using var db = database.Factory.CreateDbContext(); db.ActivityEvents.Add(new() { InstallationId = TestInstallation.Id, RuleId = 1, DeviceId = rules.Rule.EntityId, Kind = "rule.checked", OccurredAt = decision.EvaluatedAt.UtcDateTime, RecordedAt = decision.EvaluatedAt.UtcDateTime, ConfigurationVersion = RuleConfigurationVersion.Read(rules.Rule), ValuesJson = JsonSerializer.Serialize(decision) }); await db.SaveChangesAsync(); }
        public async ValueTask DisposeAsync() { await renderer.DisposeAsync(); await services.DisposeAsync(); await database.DisposeAsync(); }
    }
    private sealed class EventRenderer(IServiceProvider provider, ILoggerFactory logs) : Renderer(provider, logs)
    {
        private ComponentBase? _component;
        private ParameterView _parameters = ParameterView.Empty;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        public InstallationSettingsDto Draft => (InstallationSettingsDto)typeof(DeyeSolar.Web.Pages.Settings).GetField("_draft", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_component)!;
        public void SetSolar(SolarSiteSettings draft) => typeof(DeyeSolar.Web.Pages.Settings).GetMethod("SetSolar", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_component, [draft]);
        public TriggerRule RuleDraft => (TriggerRule)_component!.GetType().GetField("_draft", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_component)!;
        public Task InvokeAsync(string method, params object?[] arguments) => _component!.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_component, arguments) as Task ?? Task.CompletedTask;
        public void Repaint() => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_component, null);
        public async Task<int> MountAsync() { _component = (DeyeSolar.Web.Pages.Settings)InstantiateComponent(typeof(DeyeSolar.Web.Pages.Settings)); var root = AssignRootComponentId(_component); await RenderAsync(root); return root; }
        public async Task<int> MountAutomationsAsync() { _component = (ComponentBase)InstantiateComponent(typeof(DeyeSolar.Web.Pages.Automations)); _parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { ["Id"] = 1 }); var root = AssignRootComponentId(_component); await RenderAsync(root); return root; }
        public Task RenderAsync(int root) => RenderRootComponentAsync(root, _parameters);
        public Task DispatchAsync(ulong id) => DispatchEventAsync(id, null, new MouseEventArgs());
        private IEnumerable<RenderTreeFrame[]> Elements(int id, string tag)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            for (var i = 0; i < frames.Count; ++i)
            {
                var frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Component) foreach (var element in Elements(frame.ComponentId, tag)) yield return element;
                if (frame.FrameType == RenderTreeFrameType.Element && frame.ElementName == tag) yield return frames.Array.Skip(i + 1).Take(frame.ElementSubtreeLength - 1).ToArray();
            }
        }
        public (ulong Id, bool Disabled) Button(int root, string label)
        {
            var element = Assert.Single(Elements(root, "button"), e => Text(e).Trim() == label || e.Any(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "aria-label" && f.AttributeValue?.ToString() == label));
            return (element.FirstOrDefault(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onclick").AttributeEventHandlerId, Disabled(element));
        }
        private static bool Disabled(RenderTreeFrame[] e) => e.TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).Any(f => f.AttributeName == "disabled" && f.AttributeValue is true);
        public int DisabledFieldsets(int root) => Elements(root, "fieldset").Count(Disabled);
        public bool InputDisabled(int root, string label) => Disabled(Assert.Single(Elements(root, "input"), e => e.Any(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "aria-label" && f.AttributeValue?.ToString() == label)));
        public bool HasLink(int root, string href) => Elements(root, "a").Any(e => e.Any(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "href" && f.AttributeValue?.ToString() == href));
        public bool AppearanceDisabled(int root) => Disabled(Assert.Single(Elements(root, "select"), e => e.Any(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "id" && f.AttributeValue?.ToString() == "appearance")));
        public string Text(int id) { var frames = GetCurrentRenderTreeFrames(id); return Text(frames.Array.Take(frames.Count)); }
        private string Text(IEnumerable<RenderTreeFrame> frames) => WebUtility.HtmlDecode(string.Join(" ", frames.Select(f => f.FrameType switch { RenderTreeFrameType.Text => f.TextContent, RenderTreeFrameType.Markup => Regex.Replace(f.MarkupContent, "<[^>]*>", ""), RenderTreeFrameType.Component => Text(f.ComponentId), _ => "" })));
    }
    private sealed class Rules : IConfigurationRules
    {
        public TriggerRule Rule { get; } = new() { Id = 1, Name = "Read-only automation", EntityId = "synthetic-device", Enabled = false, SocTurnOnThreshold = 75, SocTurnOffThreshold = 55, UseSeparateSocTurnOffThreshold = true };
        public int Mutations { get; private set; }
        public Task<List<TriggerRule>> GetAllAsync(CancellationToken ct) => Task.FromResult(new List<TriggerRule> { Rule });
        public Task<TriggerRule?> GetByIdAsync(int id, CancellationToken ct) => Task.FromResult<TriggerRule?>(id == Rule.Id ? Rule : null);
        public Task<TriggerRule> CreateAsync(TriggerRule rule, CancellationToken ct) { ++Mutations; return Task.FromResult(rule); }
        public Task UpdateAsync(TriggerRule rule, CancellationToken ct) { ++Mutations; return Task.CompletedTask; }
        public Task DeleteAsync(int id, string version, CancellationToken ct) { ++Mutations; return Task.CompletedTask; }
    }
    private sealed class Inventory : ISocketInventoryService
    {
        public Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DevicePowerInfo>>([]);
        public Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct) => throw new InvalidOperationException("Rendering automation details must not refresh or command devices.");
    }
    private sealed class Inverters : IInverterCatalog
    {
        public Task<IReadOnlyList<InverterDescriptor>> ListRegisteredAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InverterDescriptor>>([]);
        public Task<IInverter> GetAsync(InverterId id, CancellationToken ct) => throw new InvalidOperationException("Rendering automation details must not read hardware.");
        public Task<IInverterGridHistory?> GetGridHistoryAsync(InverterId id, CancellationToken ct) => throw new InvalidOperationException("Rendering automation details must not read hardware.");
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/settings");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
        protected override void SetNavigationLockState(bool value) { }
    }
    private sealed class NullJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}

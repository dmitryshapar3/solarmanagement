using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Components.Ui;
using DeyeSolar.Web.Localization;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Tests;

public sealed class CircuitCultureTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 11, 20, 0, TimeSpan.Zero);

    [Fact]
    public async Task RealSnapshotEventKeepsRussianLabelsNumbersAndDatesOnAnEnglishWorker()
    {
        using var locale = new AmbientCulture("ru-RU");
        var clock = new ManualClock();
        await using var services = Services(clock);
        var snapshot = services.GetRequiredService<InverterDataSnapshot>();
        snapshot.Update(Reading(3700));
        using var russianScope = services.CreateScope();
        using var englishScope = services.CreateScope();
        englishScope.ServiceProvider.GetRequiredService<UiText>().InitializeCircuit("en-GB", "en-GB");
        await using var renderer = new HtmlRenderer(russianScope.ServiceProvider, services.GetRequiredService<ILoggerFactory>());
        await using var englishRenderer = new HtmlRenderer(englishScope.ServiceProvider, services.GetRequiredService<ILoggerFactory>());
        var englishRoot = await englishRenderer.Dispatcher.InvokeAsync(() => englishRenderer.RenderComponentAsync<SolarEstimateCard>(CardParameters()));
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<SolarEstimateCard>(CardParameters()));
        var before = await renderer.Dispatcher.InvokeAsync(() => VisibleText(root.ToHtmlString()));
        AssertRussian(before, "3,70");

        await OnEnglishWorker(() => snapshot.Update(Reading(3250)));
        var after = await renderer.Dispatcher.InvokeAsync(() => VisibleText(root.ToHtmlString()));
        AssertRussian(after, "3,25");
        Assert.DoesNotContain("3,70 kW", after);
        var englishAfter = await englishRenderer.Dispatcher.InvokeAsync(() => VisibleText(englishRoot.ToHtmlString()));
        Assert.Contains("Latest solar snapshot", englishAfter);
        Assert.Contains("3.25 kW", englishAfter);
        Assert.DoesNotContain("Последний снимок", englishAfter);
        Assert.DoesNotContain("3,25 kW", englishAfter);
    }

    [Fact]
    public async Task TimerCreatedInEnglishUsesTheExplicitCircuitLocaleAndRefreshesActualFreshness()
    {
        using var locale = new AmbientCulture("en-US");
        var clock = new ManualClock();
        await using var services = Services(clock);
        // Exercise App's actual initial-parameter lifecycle on an English renderer/caller.
        services.GetRequiredService<InverterDataSnapshot>().Update(Reading(3700));
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CircuitCardHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(DeyeSolar.Web.App.CultureName)] = "ru-RU", [nameof(DeyeSolar.Web.App.UiCultureName)] = "ru"
        })));
        AssertRussian(await renderer.Dispatcher.InvokeAsync(() => VisibleText(root.ToHtmlString())), "3,70");

        clock.Now = Now.AddMinutes(11);
        await OnEnglishWorker(clock.Pulse);
        // Drain the renderer after PeriodicTimer has dispatched the real card refresh.
        await WaitUntilAsync(renderer, () => VisibleText(root.ToHtmlString()).Contains("устар", StringComparison.OrdinalIgnoreCase));
        var after = await renderer.Dispatcher.InvokeAsync(() => VisibleText(root.ToHtmlString()));
        Assert.Contains("Последний снимок", after);
        Assert.Contains("сент", after);
        Assert.DoesNotContain("Latest solar snapshot", after);
        Assert.DoesNotContain("3,70 kW", after);
    }

    [Fact]
    public async Task RealNumericInputParsesItsCircuitDecimalSeparatorOnAnEnglishEvent()
    {
        using var locale = new AmbientCulture("ru-RU");
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddComponentLocalization();
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new InputRenderer(services, services.GetRequiredService<ILoggerFactory>());
        double value = 12.5;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.MountAsync(new Dictionary<string, object?>
        {
            [nameof(UiNumber.Label)] = "Power", [nameof(UiNumber.Value)] = value,
            [nameof(UiNumber.ValueChanged)] = EventCallback.Factory.Create<double>(new object(), next => value = next)
        }));
        Assert.Equal("12,5", renderer.InputValue(root));
        await OnEnglishWorker(async () => await renderer.Dispatcher.InvokeAsync(() => renderer.InputAsync(root, "13,75")));
        Assert.Equal(13.75, value);
        Assert.Equal("13,75", renderer.InputValue(root));
        Assert.False(renderer.Invalid(root));
    }

    [Fact]
    public void CircuitInitializationPreservesRegionalFormattingWithoutChangingOtherScopesOrTheWorker()
    {
        using var locale = new AmbientCulture("en-US");
        var collection = new ServiceCollection();
        collection.AddLogging(); collection.AddComponentLocalization();
        using var provider = collection.BuildServiceProvider();
        using var first = provider.CreateScope(); using var second = provider.CreateScope();
        var russian = first.ServiceProvider.GetRequiredService<UiText>();
        var english = second.ServiceProvider.GetRequiredService<UiText>();
        russian.InitializeCircuit("ru-RU", "ru");
        english.InitializeCircuit("en-GB", "en-GB");
        Assert.Equal("Настройки", russian["Settings"]);
        Assert.Equal("Settings", english["Settings"]);
        Assert.Equal("1,25", russian.Number(1.25));
        Assert.Equal("1.25", english.Number(1.25));
        Assert.Equal(Now.ToString("d MMM yyyy", CultureInfo.GetCultureInfo("en-GB")), english.Time(Now, "UTC", "d MMM yyyy"));
        russian.InitializeCircuit("ja", "ja");
        Assert.Equal("ru", russian.Language);
        Assert.Equal("en-US", CultureInfo.CurrentCulture.Name);
        Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);
    }

    private static string VisibleText(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));

    private static void AssertRussian(string html, string actual)
    {
        Assert.Contains("Последний снимок", html);
        Assert.Contains(actual + " kW", html);
        Assert.Contains("4,60 kW", html);
        Assert.Contains("сент", html);
        Assert.DoesNotContain("Latest solar snapshot", html);
        Assert.DoesNotContain("Expected now", html);
    }

    private static async Task OnEnglishWorker(Action action) => await OnEnglishWorker(() => { action(); return Task.CompletedTask; });
    private static Task OnEnglishWorker(Func<Task> action) => Task.Run(async () =>
    {
        using var locale = new AmbientCulture("en-US");
        await action();
        Assert.Equal("en-US", CultureInfo.CurrentCulture.Name);
        Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);
    });

    private static async Task WaitUntilAsync(HtmlRenderer renderer, Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await renderer.Dispatcher.InvokeAsync(condition)) await Task.Delay(10, timeout.Token);
    }

    private static InverterData Reading(int watts) => ConfirmedInverterReading.Create(new InverterData { SolarProduction = watts, SolarObservedAt = Now, SolarDeviceSn = "primary", Timestamp = Now });
    private static ParameterView CardParameters()
    {
        var observation = new SolarRadiationObservation(Now, 820, 430, 19, 2, Now, .2)
        { Kind = SolarRadiationKind.WeatherModel, RetrievedAt = Now.AddMinutes(-8) };
        var estimate = new SolarPowerEstimate(Now, Now, DeyeSolar.Domain.Models.SolarPowerBasis.PvDc, 4.6, 3.1, 6.2, 8.1, .3, false, observation, []);
        return ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(SolarEstimateCard.State)] = SolarEstimateState.Empty with { Estimate = estimate },
            [nameof(SolarEstimateCard.Options)] = new SolarEstimateOptions { TimeZoneId = "UTC", DeyeSolarPowerIsPvDcConfirmed = true, DeyeConfirmedDeviceSn = "primary" }
        });
    }

    private static ServiceProvider Services(ManualClock clock)
    {
        var services = new ServiceCollection();
        services.AddLogging(); services.AddComponentLocalization();
        services.AddSingleton<TimeProvider>(clock); services.AddSingleton<InverterDataSnapshot>();
        services.AddSingleton<IJSRuntime, NullJs>(); services.AddSingleton<NavigationManager, Navigation>();
        services.AddOptions<SolarEstimateOptions>(); services.Configure<InverterConnectionOptions>(options => options.DeviceKey = "primary");
        services.AddSingleton<ISolarRadiationSource, UnusedSource>(); services.AddSingleton<ISolarEstimateStore, UnusedStore>();
        services.AddSingleton<IInverterRefreshService, UnusedRefresh>(); services.AddSingleton<SolarEstimateService>();
        return services.BuildServiceProvider();
    }

    private sealed class AmbientCulture : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentCulture, previousUi = CultureInfo.CurrentUICulture;
        public AmbientCulture(string name) { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name); }
        public void Dispose() { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
    }
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = CircuitCultureTests.Now;
        private readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(callback, state); timers.Add(timer); return timer; }
        public void Pulse() { foreach (var timer in timers.ToArray()) timer.Pulse(); }
    }
    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
        public void Pulse() { if (!disposed) callback(state); }
        public void Dispose() => disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    // Keep the real App initialization, replacing only router/authorization I/O with the actual card.
    private sealed class CircuitCardHost : DeyeSolar.Web.App
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<SolarEstimateCard>(0);
            var sequence = 1;
            foreach (var parameter in CardParameters()) builder.AddAttribute(sequence++, parameter.Name, parameter.Value);
            builder.CloseComponent();
        }
    }
    private sealed class InputRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        public async Task<int> MountAsync(Dictionary<string, object?> parameters)
        { var root = AssignRootComponentId(InstantiateComponent(typeof(UiNumber))); await RenderRootComponentAsync(root, ParameterView.FromDictionary(parameters)); return root; }
        private RenderTreeFrame[] Frames(int root) { var frames = GetCurrentRenderTreeFrames(root); return frames.Array.Take(frames.Count).ToArray(); }
        public string? InputValue(int root) => Frames(root).First(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "value").AttributeValue?.ToString();
        public bool Invalid(int root) => Frames(root).Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-invalid" && frame.AttributeValue?.ToString() == "true");
        public Task InputAsync(int root, string text) => DispatchEventAsync(Frames(root).First(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "oninput").AttributeEventHandlerId, null, new ChangeEventArgs { Value = text });
    }
    private sealed class Navigation : NavigationManager
    { public Navigation() => Initialize("http://localhost/", "http://localhost/"); protected override void NavigateToCore(string uri, bool forceLoad) { } }
    private sealed class NullJs : IJSRuntime
    { public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => ValueTask.FromResult(default(T)!); public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!); }
    private sealed class UnusedSource : ISolarRadiationSource
    { public Task<SolarRadiationObservation> ReadAsync(SolarEstimateOptions options, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException("A supplied estimate must not request weather."); }
    private sealed class UnusedStore : ISolarEstimateStore
    { public Task<CachedSolarObservation?> LoadAsync(CancellationToken ct) => throw new NotSupportedException(); public Task SaveAsync(CachedSolarObservation observation, CancellationToken ct) => throw new NotSupportedException(); public Task<SolarActual?> FindActualAsync(DateTimeOffset timestamp, int toleranceSeconds, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException(); }
    private sealed class UnusedRefresh : IInverterRefreshService
    { public Task<InverterData> RefreshAsync(CancellationToken ct) => throw new InvalidOperationException("Rendering must not poll hardware."); }
}

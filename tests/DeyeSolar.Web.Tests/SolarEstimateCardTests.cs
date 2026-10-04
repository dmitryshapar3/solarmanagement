using SolarPowerBasis = DeyeSolar.Domain.Models.SolarPowerBasis;
using SolarManagement.Inverters.Contracts;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace DeyeSolar.Web.Tests;

public class SolarEstimateCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 11, 20, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("normal")]
    [InlineData("night")]
    [InlineData("empty")]
    [InlineData("satellite")]
    public async Task PowerCardUsesSelectedEnglishNumbersDatesAndAccessibleText(string scenario)
    {
        var fixture = CreateFixture();
        fixture = scenario switch
        {
            "night" => fixture with
            {
                State = fixture.State with { Estimate = fixture.State.Estimate! with { CentralKw = 0 } },
                Actual = fixture.Actual! with { SolarProduction = 0 }
            },
            "empty" => fixture with { State = SolarEstimateState.Empty, Actual = null },
            "satellite" => fixture with { State = fixture.State with { Estimate = fixture.State.Estimate! with
                { Observation = fixture.State.Estimate.Observation with { Kind = SolarRadiationKind.Satellite } } } },
            _ => fixture
        };
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
            var html = await RenderAsync(fixture);

            Assert.DoesNotMatch("[\\u0400-\\u04FF]", html);
            Assert.Contains("Latest solar snapshot", html);
            Assert.Contains("Data sources and timestamps", html);
            Assert.Contains("Possible", html);
            Assert.Contains("Latest inverter generation", html);
            if (scenario == "empty")
            {
                Assert.Contains("Estimate unavailable", html);
                Assert.Equal("—", ReadValue(html, "solar-actual"));
            }
            else
            {
                Assert.Matches("29 Sep(?:t)? 2026", html);
                Assert.Equal(scenario == "night" ? "0.00 kW" : "4.60 kW", ReadValue(html, "solar-possible"));
                Assert.Contains(scenario == "satellite" ? "Satellite estimate" : "Weather estimate", html);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    [Fact]
    public async Task CompactTileHasExactlyTwoPowerValues()
    {
        var fixture = CreateFixture();
        var html = await RenderAsync(fixture, "compact-now");

        Assert.Equal(2, Regex.Matches(html, "<output\\b").Count);
        Assert.Equal("4.60 kW", ReadValue(html, "solar-possible"));
        Assert.Equal("3.70 kW", ReadValue(html, "solar-actual"));
        var visible = StripTags(Regex.Replace(html, "<details\\b.*?</details>", "", RegexOptions.Singleline));
        Assert.Contains("Possible", visible);
        Assert.Contains("Latest inverter generation", visible);
        Assert.DoesNotContain("Deviation", html);
        Assert.DoesNotContain("Approximate range", html);
        Assert.DoesNotContain("Roofs and configuration", html);
        Assert.DoesNotContain("Within the expected range", html);
        Assert.DoesNotContain("Below the estimate", html);
        Assert.DoesNotContain("Above the estimate", html);
        Assert.DoesNotContain("8.8", visible);
        Assert.DoesNotContain("Geodetów", visible);
        Assert.Contains("Measured at", visible);
        Assert.Contains("Weather estimate", visible);
        Assert.Contains("<summary", html);
        Assert.Contains("aria-label=\"Data sources and timestamps\"", html);
        Assert.Contains("Open-Meteo", html);
        Assert.Contains("CC BY 4.0", html);
        Assert.Matches("29 Sep(?:t)? 2026 13:20:00 UTC[+]02:00", html);
        Assert.Matches("29 Sep(?:t)? 2026 13:19:00 UTC[+]02:00", html);
        Assert.Matches("29 Sep(?:t)? 2026 13:12:00 UTC[+]02:00", html);
    }

    [Theory]
    [InlineData("stale", "4.60 kW", "Weather data is stale")]
    [InlineData("expired", "—", "Estimate unavailable")]
    [InlineData("stopped", "—", "The calculation has stopped updating")]
    [InlineData("future", "—", "Estimate unavailable")]
    [InlineData("future-source", "—", "Estimate unavailable")]
    [InlineData("failed", "4.60 kW", "Weather refresh failed")]
    [InlineData("empty", "—", "Estimate unavailable")]
    public async Task EstimateFreshnessIsPreservedWithoutExtraReadings(string scenario, string expected, string reason)
    {
        var fixture = CreateFixture();
        var estimate = fixture.State.Estimate!;
        fixture = scenario switch
        {
            "stale" => fixture with { State = fixture.State with { Estimate = estimate with { Observation = estimate.Observation with { RetrievedAt = Now.AddMinutes(-21) } } } },
            "expired" => fixture with { State = fixture.State with { Estimate = estimate with { Observation = estimate.Observation with { RetrievedAt = Now.AddMinutes(-31) } } } },
            "stopped" => fixture with { State = fixture.State with { Estimate = estimate with { Timestamp = Now.AddMinutes(-3) } } },
            "future" => fixture with { State = fixture.State with { Estimate = estimate with { Timestamp = Now.AddMinutes(1) } } },
            "future-source" => fixture with { State = fixture.State with { Estimate = estimate with { Observation = estimate.Observation with { RetrievedAt = Now.AddMinutes(1) } } } },
            "failed" => fixture with { State = fixture.State with { RefreshFailed = true } },
            "empty" => fixture with { State = SolarEstimateState.Empty },
            _ => fixture
        };

        var html = await RenderAsync(fixture, "compact-" + scenario);

        Assert.Equal(2, Regex.Matches(html, "<output\\b").Count);
        Assert.Equal(expected, ReadValue(html, "solar-possible"));
        Assert.Equal("3.70 kW", ReadValue(html, "solar-actual"));
        Assert.Contains(reason, html);
        Assert.Contains("solar-info-warning", html);
    }

    [Theory]
    [InlineData("stale", "The inverter reading is stale")]
    [InlineData("missing-time", "The inverter measurement time is unverified")]
    [InlineData("future", "The inverter measurement time is invalid")]
    [InlineData("negative", "The inverter reading is unavailable")]
    [InlineData("missing", "The inverter reading is unavailable")]
    public async Task UnavailableActualDoesNotBecomeZeroOrAStaleCurrentValue(string scenario, string reason)
    {
        var fixture = CreateFixture();
        fixture = fixture with
        {
            Actual = scenario switch
            {
                "stale" => fixture.Actual! with { SolarObservedAt = Now.AddMinutes(-11) },
                "missing-time" => fixture.Actual! with { SolarObservedAt = null },
                "future" => fixture.Actual! with { SolarObservedAt = Now.AddMinutes(1) },
                "negative" => fixture.Actual! with { SolarProduction = -1 },
                _ => null
            }
        };

        var html = await RenderAsync(fixture, "compact-actual-" + scenario);

        Assert.Equal("4.60 kW", ReadValue(html, "solar-possible"));
        Assert.Equal("—", ReadValue(html, "solar-actual"));
        Assert.Contains(reason, html);
        Assert.Contains("solar-info-warning", html);
    }

    [Theory]
    [InlineData(false, "test-device")]
    [InlineData(true, "different-device")]
    public async Task UnconfirmedDeviceNeverGetsAComparisonVerdict(bool confirmed, string confirmedDevice)
    {
        var fixture = CreateFixture();
        fixture.Options.DeyeSolarPowerIsPvDcConfirmed = confirmed;
        fixture.Options.DeyeConfirmedDeviceSn = confirmedDevice;

        var html = await RenderAsync(fixture, "compact-unconfirmed");

        Assert.Equal("4.60 kW", ReadValue(html, "solar-possible"));
        Assert.Equal("3.70 kW", ReadValue(html, "solar-actual"));
        Assert.Contains("The inverter power type is unconfirmed", html);
        Assert.DoesNotContain("Within the expected range", html);
        Assert.DoesNotContain("Deviation", html);
        Assert.Contains("solar-info-warning", html);
    }

    [Theory]
    [InlineData("other-device", "The reading belongs to another inverter")]
    [InlineData(null, "The inverter reading's device is unverified")]
    public async Task ForeignOrUnverifiedDeviceSnapshotIsNeverPresentedAsThisInstallation(string? measuredDevice, string reason)
    {
        var fixture = CreateFixture();
        fixture = fixture with { Actual = fixture.Actual! with { SolarDeviceSn = measuredDevice } };

        var html = await RenderAsync(fixture, "compact-foreign-device");

        Assert.Equal("4.60 kW", ReadValue(html, "solar-possible"));
        Assert.Equal("—", ReadValue(html, "solar-actual"));
        Assert.Contains(reason, html);
    }

    [Fact]
    public async Task NightKeepsRealZerosAndNoPercentages()
    {
        var fixture = CreateFixture();
        fixture = fixture with
        {
            State = fixture.State with { Estimate = fixture.State.Estimate! with { CentralKw = 0, LowerKw = 0, UpperKw = 0 } },
            Actual = fixture.Actual! with { SolarProduction = 0 }
        };

        var html = await RenderAsync(fixture, "compact-night");

        Assert.Equal("0.00 kW", ReadValue(html, "solar-possible"));
        Assert.Equal("0.00 kW", ReadValue(html, "solar-actual"));
        Assert.DoesNotContain("%", StripTags(html));
        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("Infinity", html);
    }

    [Fact]
    public async Task SatelliteFallbackRetainsItsObservationTimeAndSource()
    {
        var fixture = CreateFixture();
        fixture = fixture with { State = fixture.State with { Estimate = fixture.State.Estimate! with
        {
            Timestamp = Now.AddMinutes(-20),
            Observation = fixture.State.Estimate.Observation with { Kind = SolarRadiationKind.Satellite, Timestamp = Now.AddMinutes(-20) }
        } } };

        var html = await RenderAsync(fixture, "compact-satellite");

        Assert.Equal("4.60 kW", ReadValue(html, "solar-possible"));
        Assert.Contains("Satellite estimate", html);
        Assert.Matches("29 Sep(?:t)? 2026 13:00:00 UTC[+]02:00", html);
        Assert.Contains("DWD / EUMETSAT", html);
    }

    [Fact]
    public async Task WarsawWinterUsesTheWinterOffset()
    {
        var winter = new DateTimeOffset(2026, 1, 29, 11, 20, 0, TimeSpan.Zero);
        var html = await RenderAsync(CreateFixture(winter));
        Assert.Contains("29 Jan 2026 12:20:00 UTC+01:00", html);
    }

    [Fact]
    public async Task ReadingsRemainAvailableExactlyAtTheirFreshnessLimits()
    {
        var fixture = CreateFixture();
        fixture = fixture with
        {
            State = fixture.State with { Estimate = fixture.State.Estimate! with
            {
                Timestamp = Now.AddMinutes(-2),
                Observation = fixture.State.Estimate.Observation with { RetrievedAt = Now.AddMinutes(-30) }
            } },
            Actual = fixture.Actual! with { SolarObservedAt = Now.AddMinutes(-10) }
        };

        var html = await RenderAsync(fixture);

        Assert.Equal("4.60 kW", ReadValue(html, "solar-possible"));
        Assert.Equal("3.70 kW", ReadValue(html, "solar-actual"));
    }

    [Fact]
    public async Task DetailedGenerationLeadsWithTheInteractiveChartAndKeepsMissingHoursInItsTable()
    {
        var fixture = CreateFixture();
        var history = new SolarHistoryResult(Now.AddHours(-2), Now, "Europe/Warsaw",
            [new(Now.AddHours(-2), new(0, 0), 0), new(Now.AddHours(-1), null, null)])
        { Today = DateOnly.FromDateTime(Now.DateTime), SelectedDate = DateOnly.FromDateTime(Now.DateTime) };
        var html = await RenderAsync(fixture, detailed: true, history: history);

        Assert.True(html.IndexOf("data-testid=\"actual-series\"", StringComparison.Ordinal) < html.IndexOf("Latest solar snapshot", StringComparison.Ordinal));
        Assert.Contains("aria-label=\"Chart period\"", html);
        Assert.Contains("aria-label=\"Day navigation\"", html);
        foreach (var period in new[] { "Day", "7 days", "30 days" }) Assert.Matches($">{period}</button>", html);
        Assert.Contains("Hourly generation data", html);
        Assert.Matches("<td[^>]*>0[.]00</td>", html);
        Assert.Matches("<td[^>]*>—</td>", html);
        Assert.Contains("Time-aligned comparison", html);
        Assert.Equal("3.70 kW", ReadValue(html, "solar-actual"));
        Assert.Contains("8.80 kW", html);
        Assert.DoesNotContain("Estimate for comparison", html); // The weather model has no bracket for the historical sample.
        Assert.Contains("Comparison unavailable", html);
    }

    [Fact]
    public async Task TimeAlignedModelUsesOnlyTheExplicitComparisonEstimate()
    {
        var fixture = CreateFixture();
        fixture = fixture with { State = fixture.State with { ComparisonEstimate = fixture.State.Estimate! with
            { Timestamp = Now.AddMinutes(-5), CentralKw = 5.25 } } };
        var html = await RenderAsync(fixture, detailed: true);

        Assert.Contains("Estimate for comparison", html);
        Assert.Contains("5.25 kW", html);
        Assert.Contains("Above estimate range", html);
        Assert.Equal("4.60 kW", ReadValue(html, "solar-possible"));
        Assert.Equal("3.70 kW", ReadValue(html, "solar-actual"));
    }

    private sealed record Fixture(SolarEstimateState State, InverterData? Actual, SolarEstimateOptions Options, DateTimeOffset Now);

    private static Fixture CreateFixture(DateTimeOffset? at = null)
    {
        var now = at ?? Now;
        var observation = new SolarRadiationObservation(now, 820, 430, 19, 2, now, 0.2)
        {
            Kind = SolarRadiationKind.WeatherModel, RetrievedAt = now.AddMinutes(-8)
        };
        var estimate = new SolarPowerEstimate(now, now, SolarPowerBasis.PvDc, 4.6, 3.1, 6.2, 8.1, 0.3, false, observation, []);
        // A different historical reading must never replace the current snapshot in the compact tile.
        var historic = new SolarActual(now.AddMinutes(-5), 8.8, SolarPowerBasis.PvDc);
        var state = new SolarEstimateState(estimate, new(SolarComparisonStatus.AboveEstimate, historic, 5.7, 183, null), false, now.AddMinutes(-8), null);
        return new(state, new InverterData { SolarProduction = 3700, SolarObservedAt = now.AddMinutes(-1), SolarDeviceSn = "test-device", Timestamp = now },
            new SolarEstimateOptions { DeyeSolarPowerIsPvDcConfirmed = true, DeyeConfirmedDeviceSn = "test-device" }, now);
    }

    private static string StripTags(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " ")), "\\s+", " ").Trim();

    private static string ReadValue(string html, string id)
    {
        var match = Regex.Match(html, "<output\\b[^>]*data-testid=\"" + id + "\"[^>]*>(.*?)</output>", RegexOptions.Singleline);
        Assert.True(match.Success, "Missing power output: " + id);
        return StripTags(match.Groups[1].Value);
    }

    private static async Task<string> RenderAsync(Fixture fixture, string? scenario = null, bool detailed = false, SolarHistoryResult? history = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddComponentLocalization();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NullJsRuntime>();
        services.AddOptions<SolarEstimateOptions>();
        services.Configure<InverterConnectionOptions>(o => o.DeviceKey = "test-device");
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton<InverterDataSnapshot>();
        services.AddSingleton<IInverterRefreshService, UnusedRefresh>();
        services.AddSingleton<ISolarHistoryService, UnusedHistory>();
        services.AddSingleton<ISolarRadiationSource, UnusedSource>();
        services.AddSingleton<ISolarEstimateStore, UnusedStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SolarEstimateService>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SolarEstimateCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["State"] = fixture.State, ["Options"] = fixture.Options, ["Now"] = fixture.Now, ["Actual"] = fixture.Actual,
                ["Detailed"] = detailed, ["HistoryData"] = history
            }));
            return output.ToHtmlString();
        });

        if (scenario is not null && Environment.GetEnvironmentVariable("SOLAR_RENDER_OUTPUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            var theme = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = await renderer.RenderComponentAsync<MudThemeProvider>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["IsDarkMode"] = true,
                    ["Theme"] = new MudTheme { PaletteDark = new PaletteDark {
                        Primary = "#42a5f5", Secondary = "#ffb74d", Success = "#66bb6a",
                        Surface = "#1e1e2e", Background = "#121212", AppbarBackground = "#1e1e2e" } }
                }));
                return result.ToHtmlString();
            });
            var page = "<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>"
                + "<link rel='stylesheet' href='mud.css'><link rel='stylesheet' href='solar.css'>"
                + "<body style='margin:0;padding:16px;background:#121212'><div style='margin:0 auto;max-width:640px'>"
                + theme + html + "</div></body></html>";
            await File.WriteAllTextAsync(Path.Combine(folder, scenario + ".html"), page);
        }
        var decoded = WebUtility.HtmlDecode(html);
        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en")
            Assert.DoesNotMatch("[\\u0400-\\u04FF]", decoded);
        return decoded;
    }

    private sealed class NullJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class UnusedRefresh : IInverterRefreshService
    {
        public Task<InverterData> RefreshAsync(CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class UnusedHistory : ISolarHistoryService
    {
        public Task<SolarHistoryResult> ReadAsync(SolarHistoryPeriod period, CancellationToken ct, DateOnly? endDate = null) => throw new NotSupportedException();
    }
    private sealed class UnusedSource : ISolarRadiationSource
    {
        public Task<SolarRadiationObservation> ReadAsync(SolarEstimateOptions options, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class UnusedStore : ISolarEstimateStore
    {
        public Task<CachedSolarObservation?> LoadAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(CachedSolarObservation observation, CancellationToken ct) => throw new NotSupportedException();
        public Task<SolarActual?> FindActualAsync(DateTimeOffset timestamp, int toleranceSeconds, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    }
}

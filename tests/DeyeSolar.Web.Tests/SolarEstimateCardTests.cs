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

namespace DeyeSolar.Web.Tests;

public class SolarEstimateCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 11, 20, 0, TimeSpan.Zero);

    [Fact]
    public async Task HomeHeroComparesFreshCurrentPvAndAlwaysAttributesItsForecast()
    {
        var html = await RenderAsync(CreateFixture(), hero: true);
        Assert.Equal("Within estimate range", ReadValue(html,"hero-comparison"));
        Assert.DoesNotContain("Above estimate range", html);
        Assert.Contains("Weather data by Open-Meteo.com, CC BY 4.0", html);
        Assert.Contains("href=\"https://open-meteo.com/\"", html);
    }

    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("stale")]
    [InlineData("ac")]
    public async Task HomeHeroCannotCompareUnconfirmedStaleOrAcObservations(string scenario)
    {
        var fixture = CreateFixture();
        if (scenario == "unconfirmed") fixture.Options.DeyeSolarPowerIsPvDcConfirmed = false;
        fixture = scenario switch
        {
            "stale" => fixture with { Actual = fixture.Actual! with { SolarObservedAt = Now.AddMinutes(-11) } },
            "ac" => fixture with { State = fixture.State with { Estimate = fixture.State.Estimate! with { Basis = SolarPowerBasis.Ac } } },
            _ => fixture
        };
        Assert.Equal("Comparison unavailable", ReadValue(await RenderAsync(fixture, hero: true),"hero-comparison"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeNightKeepsAstronomySeparateFromVerifiedBatterySupply(bool batterySupply)
    {
        var fixture = CreateFixture(new(2026, 10, 6, 2, 0, 0, TimeSpan.Zero));
        var html = await RenderAsync(fixture, hero: true, production: HomePresentationTests.Day(), batterySupply: batterySupply);
        Assert.Contains("solar-night", html);
        Assert.Contains("Next sunrise 6 Oct 06:00", html);
        Assert.Equal(batterySupply, html.Contains("The sun has set. Home runs on battery.", StringComparison.Ordinal));
        Assert.Contains("CC BY 4.0", html);
    }

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
            Assert.Contains("Expected now", html);
            Assert.Contains(scenario == "empty" ? "Awaiting reading" : "Inverter reading", html);
            if (scenario == "empty")
            {
                Assert.Contains("The estimate is unavailable", html);
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

        Assert.Equal(2, Regex.Matches(html, "data-testid=\"solar-(?:actual|possible)\"").Count);
        Assert.Equal("4.60 kW", ReadValue(html, "solar-possible"));
        Assert.Equal("3.70 kW", ReadValue(html, "solar-actual"));
        var visible = StripTags(Regex.Replace(html, "<details\\b.*?</details>", "", RegexOptions.Singleline));
        Assert.Contains("Expected now", visible);
        Assert.Contains("Inverter reading", visible);
        Assert.DoesNotContain("Deviation", html);
        Assert.DoesNotContain("Approximate range", html);
        Assert.DoesNotContain("Roofs and configuration", html);
        Assert.DoesNotContain("Within the expected range", html);
        Assert.DoesNotContain("Below the estimate", html);
        Assert.DoesNotContain("Above the estimate", html);
        Assert.DoesNotContain("8.80", visible);
        Assert.Contains("Geodetów", visible);
        Assert.Contains("Measured at", visible);
        Assert.Contains("Weather estimate", visible);
        Assert.Contains("<summary", html);
        Assert.Contains(">Data sources and timestamps</summary>", html);
        Assert.Contains("Open-Meteo", html);
        Assert.Contains("CC BY 4.0", html);
        Assert.Matches("29 Sep(?:t)? 2026 13:20:00 UTC[+]02:00", html);
        Assert.Matches("29 Sep(?:t)? 2026 13:19:00 UTC[+]02:00", html);
        Assert.Matches("29 Sep(?:t)? 2026 13:12:00 UTC[+]02:00", html);
    }

    [Theory]
    [InlineData("stale", "4.60 kW", "Weather data is stale")]
    [InlineData("expired", "—", "The estimate is unavailable")]
    [InlineData("stopped", "—", "The calculation has stopped updating")]
    [InlineData("future", "—", "The estimate is unavailable")]
    [InlineData("future-source", "—", "The estimate is unavailable")]
    [InlineData("failed", "4.60 kW", "Weather refresh failed")]
    [InlineData("empty", "—", "The estimate is unavailable")]
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

        Assert.Equal(2, Regex.Matches(html, "data-testid=\"solar-(?:actual|possible)\"").Count);
        Assert.Equal(expected, ReadValue(html, "solar-possible"));
        Assert.Equal("3.70 kW", ReadValue(html, "solar-actual"));
        Assert.Contains(reason, html);
        Assert.DoesNotContain("Refresh", html);
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
        Assert.DoesNotContain("Refresh", html);
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
        Assert.DoesNotContain("Refresh", html);
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
    public async Task HistoricalComparisonNeverReplacesTheCurrentSnapshot()
    {
        var fixture = CreateFixture();
        var history = new SolarHistoryResult(Now.AddHours(-2), Now, "Europe/Warsaw",
            [new(Now.AddHours(-2), new(0, 0), 0), new(Now.AddHours(-1), null, null)]);
        var html = await RenderAsync(fixture, detailed: true, history: history);

        Assert.Equal("3.70 kW", ReadValue(html, "solar-actual"));
        Assert.Equal("4.60 kW", ReadValue(html, "solar-possible"));
        Assert.Contains("Historical inverter power", html);
        Assert.Contains("8.80 kW", html);
        Assert.Contains("Comparison unavailable", html);
        Assert.DoesNotContain("Inverter minus estimate", html);
        Assert.Contains("href=\"/energy\"", html);
    }

    [Theory]
    [InlineData(false, 3700)]
    [InlineData(false, 0)]
    public async Task UnverifiedPowerCannotBecomeAUsableReading(bool valid, int watts)
    {
        var fixture = CreateFixture();
        fixture = fixture with { Actual = fixture.Actual! with { Telemetry = null, SolarProduction = watts } };
        var html = await RenderAsync(fixture);
        Assert.Equal("—", ReadValue(html, "solar-actual"));
        Assert.Contains("Awaiting reading", html);
        Assert.Equal("4.60 kW", ReadValue(html, "solar-possible"));
    }

    [Fact]
    public async Task TimeAlignedModelUsesOnlyTheExplicitComparisonEstimate()
    {
        var fixture = CreateFixture();
        fixture = fixture with { State = fixture.State with { ComparisonEstimate = fixture.State.Estimate! with
            { Timestamp = Now.AddMinutes(-5), CentralKw = 5.25 } } };
        var html = await RenderAsync(fixture, detailed: true);

        Assert.Contains("Inverter minus estimate", html);
        Assert.Contains("5.70 kW", html);
        Assert.DoesNotContain("5.25 kW", html);
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
        return new(state, ConfirmedInverterReading.Create(new InverterData { SolarProduction = 3700, SolarObservedAt = now.AddMinutes(-1), SolarDeviceSn = "test-device", Timestamp = now }),
            new SolarEstimateOptions { DeyeSolarPowerIsPvDcConfirmed = true, DeyeConfirmedDeviceSn = "test-device" }, now);
    }

    private static string StripTags(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " ")), "\\s+", " ").Trim();

    private static string ReadValue(string html, string id)
    {
        var match = Regex.Match(html, "<(div|strong|p)\\b[^>]*data-testid=\"" + id + "\"[^>]*>(.*?)</\\1>", RegexOptions.Singleline);
        Assert.True(match.Success, "Missing power output: " + id);
        var value = StripTags(match.Groups[2].Value);
        return value.StartsWith("—", StringComparison.Ordinal) ? "—" : value;

    }

    private static async Task<string> RenderAsync(Fixture fixture, string? scenario = null, bool detailed = false, SolarHistoryResult? history = null,
        bool hero = false, DeyeSolar.Web.Redesign.ProductionViewDto? production = null, bool batterySupply = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddComponentLocalization();
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
                ["Detailed"] = detailed, ["HistoryData"] = history, ["Hero"] = hero,
                ["ProductionData"] = production, ["HomeRunsOnBattery"] = batterySupply
            }));
            return output.ToHtmlString();
        });

        await RenderPreview.ExportAsync(renderer, html, scenario, 640);
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

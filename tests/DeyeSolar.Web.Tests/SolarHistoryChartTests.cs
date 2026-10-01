using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace DeyeSolar.Web.Tests;

public class SolarHistoryChartTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 29);

    [Theory]
    [InlineData(false, "normal")]
    [InlineData(true, "normal")]
    [InlineData(true, "zero")]
    [InlineData(true, "missing")]
    [InlineData(true, "weather-error")]
    [InlineData(true, "actual-error")]
    [InlineData(true, "empty")]
    public async Task GenerationViewsRemainEnglishAndPreserveAvailableSeries(bool overview, string scenario)
    {
        var data = scenario switch
        {
            "zero" => RangeResult((0, 0, 0)),
            "missing" => Result((null, null)),
            "weather-error" => Result((null, 2)) with { WeatherError = "Weather history is unavailable." },
            "actual-error" => Result((2, null)) with { ActualError = "Inverter history is unavailable." },
            "empty" => Result(),
            _ => RangeResult((1.2, 3.4, 2.1), (1.3, 3.5, 2.2))
        };
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
            var html = await RenderAsync(data, overview: overview);

            Assert.DoesNotMatch("[\\u0400-\\u04FF]", html);
            Assert.Contains("Solar generation", html);
            Assert.Contains("Hourly average power", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("aria-label=\"Refresh chart\"", html);
            if (overview)
            {
                Assert.Contains("href=\"/generation\"", html);
                Assert.Contains("View details", html);
                Assert.Contains("Today · hourly average power · kW", html);
                Assert.DoesNotContain("aria-label=\"Day navigation\"", html);
                Assert.DoesNotContain("aria-label=\"Chart period\"", html);
                Assert.DoesNotContain("<details", html);
                Assert.Contains("Weather by Open-Meteo", html);
            }
            else
            {
                Assert.Contains("aria-label=\"Day navigation\"", html);
                Assert.Contains("aria-label=\"Chart period\"", html);
                Assert.Contains("About this chart", html);
                Assert.Contains("29 September 2026", html);
            }
            if (scenario is "normal" or "zero" or "weather-error" or "actual-error")
            {
                Assert.Contains("data-testid=\"possible-band\"", html);
                Assert.Contains("data-testid=\"actual-series\"", html);
                Assert.Contains("aria-label=\"Previous hour\"", html);
                Assert.Contains("aria-label=\"Next hour\"", html);
                if (scenario == "normal") Assert.Contains("possible 1.3–3.5 kW, actual 2.2 kW", html);
                if (scenario == "zero") Assert.Contains("possible 0.0–0.0 kW, actual 0.0 kW", html);
                if (scenario == "weather-error") Assert.Contains("Weather history is unavailable.", html);
                if (scenario == "actual-error") Assert.Contains("Inverter history is unavailable.", html);
            }
            else
            {
                Assert.DoesNotContain("data-testid=\"possible-band\"", html);
                Assert.Contains(scenario == "empty" ? "first completed hour" : "No data is available", html);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    [Fact]
    public async Task OverviewLoadsTodayAndHourInspectionDoesNotChangeTheRequestedPeriod()
    {
        var history = new RecordingHistoryService();
        await using var services = InteractiveServices(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync(overview: true);
            Assert.Single(history.Calls);
            Assert.Equal(SolarHistoryPeriod.Today, history.Calls[0].Period);
            Assert.Null(history.Calls[0].Date);
            await renderer.ClickAsync(root, "Previous hour");
            Assert.Single(history.Calls);
            await renderer.ClickAsync(root, "Refresh chart");
            Assert.Equal(2, history.Calls.Count);
            Assert.All(history.Calls, call =>
            {
                Assert.Equal(SolarHistoryPeriod.Today, call.Period);
                Assert.Null(call.Date);
            });
        });
    }

    [Fact]
    public async Task OverviewKeepsTheLoadedDateAcrossMidnightUntilRefreshCompletes()
    {
        var clock = new FixedClock { Now = new(2026, 9, 29, 21, 59, 0, TimeSpan.Zero) };
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var history = new RecordingHistoryService
        {
            DateProvider = () => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.Now, zone).DateTime)
        };
        await using var services = InteractiveServices(history, clock);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync(overview: true);
            clock.Now = clock.Now.AddMinutes(2);
            await renderer.ClickAsync(root, "Previous hour");
            Assert.Matches("29 Sep(?:t)? 2026 · hourly average power", renderer.Text(root));
            Assert.DoesNotContain("Today · hourly average power", renderer.Text(root));

            await renderer.ClickAsync(root, "Refresh chart");
            Assert.Contains("Today · hourly average power", renderer.Text(root));
            Assert.DoesNotMatch("29 Sep(?:t)? 2026 · hourly average power", renderer.Text(root));
        });
    }

    [Fact]
    public async Task HeaderExposesDayNavigationSeparateFromHourInspection()
    {
        var html = await RenderAsync(Result((1, 2), (3, 4)));

        Assert.Contains("aria-label=\"Previous day\"", html);
        Assert.Contains("aria-label=\"Next day\"", html);
        Assert.Contains("aria-label=\"Previous hour\"", html);
        Assert.Contains("aria-label=\"Next hour\"", html);
    }

    [Theory]
    [InlineData(0, false, true, "29 September 2026")]
    [InlineData(1, false, false, "28 September 2026")]
    [InlineData(29, true, false, "31 August 2026")]
    public async Task DayNavigationShowsTheDateAndHonorsBothBounds(int daysAgo, bool previousDisabled, bool nextDisabled, string caption)
    {
        var date = Today.AddDays(-daysAgo);
        var html = await RenderAsync(ResultForDate(date), "history-date-" + daysAgo);

        Assert.Equal(previousDisabled, DisabledButton(html, "Previous day"));
        Assert.Equal(nextDisabled, DisabledButton(html, "Next day"));
        Assert.Contains(caption, html);
        Assert.Matches("<button[^>]*aria-pressed=\"true\"[^>]*>Day</button>", html);
        Assert.DoesNotMatch("<button[^>]*aria-pressed=\"true\"[^>]*>Today</button>", html);
    }

    [Fact]
    public async Task EmptyPastDayDoesNotPromiseAFirstHourInTheFuture()
    {
        var html = await RenderAsync(ResultForDate(Today.AddDays(-1)) with { Points = [] }, "history-empty-past");

        Assert.Contains("28 September 2026", html);
        Assert.Contains("No data is available for this day yet.", html);
        Assert.DoesNotContain("after the first completed hour of the day", html);
    }

    [Fact]
    public async Task DayButtonsRequestAnotherDateWhileHourButtonsOnlyInspectTheLoadedDay()
    {
        var history = new RecordingHistoryService();
        await using var services = InteractiveServices(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            Assert.Single(history.Calls);
            Assert.Null(history.Calls[0].Date);

            await renderer.ClickAsync(root, "Previous day");
            Assert.Equal(Today.AddDays(-1), history.Calls[1].Date);
            var requestCount = history.Calls.Count;
            await renderer.ClickAsync(root, "Previous hour");
            Assert.Equal(requestCount, history.Calls.Count);

            await renderer.ClickAsync(root, "7 days");
            Assert.Equal((SolarHistoryPeriod.Week, Today.AddDays(-1)), (history.Calls[^1].Period, history.Calls[^1].Date));
            await renderer.ClickAsync(root, "Refresh chart");
            Assert.Equal(Today.AddDays(-1), history.Calls[^1].Date);
            await renderer.ClickAsync(root, "Today");
            Assert.Null(history.Calls[^1].Date);
            Assert.True(renderer.Button(root, "Next day").Disabled);
        });
    }

    [Fact]
    public async Task FastDayNavigationCancelsAndFencesAnOlderResponse()
    {
        var history = new RecordingHistoryService { HoldPastRequests = true };
        await using var services = InteractiveServices(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            var olderClick = renderer.ClickAsync(root, "Previous day");
            Assert.Equal(Today.AddDays(-1), history.Calls[1].Date);
            Assert.False(renderer.Button(root, "Previous day").Disabled);
            var latestClick = renderer.ClickAsync(root, "Previous day");
            Assert.Equal(Today.AddDays(-2), history.Calls[2].Date);
            Assert.True(history.Calls[1].Token.IsCancellationRequested);

            history.Complete(Today.AddDays(-2));
            await latestClick;
            history.Complete(Today.AddDays(-1));
            await olderClick;
            Assert.Contains("27 September 2026", renderer.Text(root));
            Assert.DoesNotContain("28 September 2026", renderer.Text(root));
        });
    }

    [Fact]
    public async Task MidnightDoesNotRelabelTheLoadedDayBeforeRefresh()
    {
        var clock = new FixedClock { Now = new(2026, 9, 29, 21, 59, 0, TimeSpan.Zero) };
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var history = new RecordingHistoryService
        {
            DateProvider = () => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.Now, zone).DateTime)
        };
        await using var services = InteractiveServices(history, clock);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            clock.Now = clock.Now.AddMinutes(2);
            await renderer.ClickAsync(root, "Previous hour");
            Assert.Contains("29 September 2026", renderer.Text(root));
            Assert.DoesNotContain("30 September 2026", renderer.Text(root));
            Assert.False(renderer.Button(root, "Next day").Disabled);

            await renderer.ClickAsync(root, "Refresh chart");
            Assert.Null(history.Calls[^1].Date);
            Assert.Contains("30 September 2026", renderer.Text(root));
            Assert.DoesNotContain("29 September 2026", renderer.Text(root));
        });
    }

    [Fact]
    public async Task EmptyTodayCanNavigateAndRetryAFailedPastDayWithoutLosingTheDate()
    {
        var history = new RecordingHistoryService { EmptyToday = true, FailFirstPastRequest = true };
        await using var services = InteractiveServices(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            Assert.Contains("The chart will appear after the first completed hour of the day", renderer.Text(root));
            Assert.False(renderer.Button(root, "Previous day").Disabled);
            await renderer.ClickAsync(root, "Previous day");
            Assert.Contains("The chart could not be loaded", renderer.Text(root));
            Assert.Equal(Today.AddDays(-1), history.Calls[^1].Date);
            Assert.False(renderer.Button(root, "Refresh chart").Disabled);

            await renderer.ClickAsync(root, "Refresh chart");
            Assert.Equal(Today.AddDays(-1), history.Calls[^1].Date);
            Assert.Contains("28 September 2026", renderer.Text(root));
            Assert.DoesNotContain("The chart could not be loaded", renderer.Text(root));
            Assert.False(renderer.Button(root, "Next day").Disabled);
        });
    }

    [Fact]
    public async Task PossiblePowerUsesBothBoundsAndUpperBoundScaleInsteadOfACentralLine()
    {
        var html = await RenderAsync(RangeResult((1, 6, 0.5), (2, 5, 1)), "history-range");
        var band = Path(html, "possible-band");
        var vertices = Vertices(band);

        Assert.Single(Regex.Matches(band, "\\bM\\b"));
        Assert.Single(Regex.Matches(band, "\\bZ\\b"));
        Assert.Contains((270d, 28d), vertices);
        Assert.Contains((270d, 198d), vertices);
        Assert.Contains((714d, 62d), vertices);
        Assert.Contains((714d, 164d), vertices);
        Assert.All(vertices, vertex => Assert.InRange(vertex.Y, 28, 232));
        Assert.Equal("M 270 215 L 714 198", Path(html, "actual-series").Trim());
        Assert.Contains("Possible · range", html);
        Assert.DoesNotContain("possible-series", html);
        Assert.DoesNotContain("possible-dot", html);
        Assert.Matches("class=\"possible-value\"[^>]*>Possible <strong[^>]*>2[.]0–5[.]0 kW</strong>", html);
        Assert.Matches("class=\"actual-value\"[^>]*>Actual <strong[^>]*>1[.]0 kW</strong>", html);
    }

    [Fact]
    public async Task GapsSplitTheBandAndActualLineIndependentlyAndExposeMissingValues()
    {
        var data = RangeResult((1, 2, 2), (2, 3, null), (null, null, 3), (4, 5, 4), (5, 6, null));
        var html = await RenderAsync(data, "history-gaps");
        var possible = Path(html, "possible-band");
        var actual = Path(html, "actual-series");
        var segments = Regex.Matches(possible, "\\bM\\b[^M]*?\\bZ\\b").Select(match => Vertices(match.Value)).ToArray();

        Assert.Equal(2, Regex.Matches(possible, "\\bM\\b").Count);
        Assert.Equal(2, Regex.Matches(possible, "\\bZ\\b").Count);
        Assert.Equal(2, segments.Length);
        Assert.Equal(48, segments[0].Min(vertex => vertex.X));
        Assert.Equal(403.2, segments[0].Max(vertex => vertex.X));
        Assert.Equal(580.8, segments[1].Min(vertex => vertex.X));
        Assert.Equal(936, segments[1].Max(vertex => vertex.X));
        Assert.Equal(2, Regex.Matches(actual, "\\bM\\b").Count);
        Assert.Single(Regex.Matches(actual, "\\bL\\b"));
        Assert.Contains("Gaps indicate insufficient saved readings for the hour", html);
        Assert.Matches("class=\"actual-value\"[^>]*>Actual <strong[^>]*>—</strong>", html);
        Assert.Contains("possible —, actual 3.0 kW", html);
        Assert.Contains("actual —", html);
    }

    [Fact]
    public async Task AnIsolatedPossibleHourFillsOnlyItsOwnHourWithBothBounds()
    {
        var html = await RenderAsync(RangeResult((null, null, null), (1, 3, null), (null, null, null)), "history-isolated-range");
        var band = Path(html, "possible-band");
        var vertices = Vertices(band);

        Assert.Single(Regex.Matches(band, "\\bM\\b"));
        Assert.Single(Regex.Matches(band, "\\bZ\\b"));
        Assert.Equal(344, vertices.Min(vertex => vertex.X));
        Assert.Equal(640, vertices.Max(vertex => vertex.X));
        Assert.Contains((344d, 79d), vertices);
        Assert.Contains((640d, 79d), vertices);
        Assert.Contains((344d, 181d), vertices);
        Assert.Contains((640d, 181d), vertices);
        Assert.All(vertices, vertex => Assert.InRange(vertex.X, 344, 640));
        Assert.Empty(Path(html, "actual-series"));
        Assert.Contains("possible 1.0–3.0 kW, actual —", html);
    }

    [Fact]
    public async Task RealZeroPowerRendersOnTheBaselineInsteadOfBecomingAGap()
    {
        var html = await RenderAsync(RangeResult((0, 0, 0)), "history-zero");
        var band = Path(html, "possible-band");
        var vertices = Vertices(band);

        Assert.Single(Regex.Matches(band, "\\bM\\b"));
        Assert.Single(Regex.Matches(band, "\\bZ\\b"));
        Assert.NotEmpty(vertices);
        Assert.All(vertices, vertex => Assert.Equal(232, vertex.Y));
        Assert.Equal(48, vertices.Min(vertex => vertex.X));
        Assert.Equal(936, vertices.Max(vertex => vertex.X));
        Assert.Equal("M 492 232", Path(html, "actual-series").Trim());
        Assert.Matches("class=\"possible-value\"[^>]*>Possible <strong[^>]*>0[.]0–0[.]0 kW</strong>", html);
        Assert.Matches("class=\"actual-value\"[^>]*>Actual <strong[^>]*>0[.]0 kW</strong>", html);
        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("Infinity", html);
        Assert.DoesNotContain("No data is available for this period yet", html);
    }

    [Fact]
    public async Task ControlsInspectorAndPointTooltipsDescribePowerAndLocalTime()
    {
        var html = await RenderAsync(RangeResult((1, 2, 0.8), (3.2, 5.6, 3.7)), "history-now");

        Assert.Contains("aria-label=\"Chart period\"", html);
        Assert.Matches("<button[^>]*aria-pressed=\"[Tt]rue\"[^>]*>Day</button>", html);
        Assert.Contains(">7 days</button>", html);
        Assert.Contains(">30 days</button>", html);
        Assert.Contains("aria-label=\"Refresh chart\"", html);
        Assert.Contains("aria-label=\"Previous hour\"", html);
        Assert.Contains("aria-label=\"Next hour\"", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("Hourly average power · kW", html);
        Assert.Matches("29 Sep(?:t)?" + Regex.Escape(" · 09:00–10:00 (UTC+02:00)"), html);
        Assert.Contains("possible 1.0–2.0 kW, actual 0.8 kW", html);
        Assert.Contains("possible 3.2–5.6 kW, actual 3.7 kW", html);
        Assert.Contains("role=\"img\"", html);
        Assert.Contains("aria-label=\"Hourly possible solar power range and actual power\"", html);
        Assert.DoesNotContain("kWh", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialSourceFailureKeepsTheOtherSeries(bool weatherFailed)
    {
        var data = weatherFailed
            ? Result((null, 2), (null, 3)) with { WeatherError = "Weather history is unavailable." }
            : Result((2, null), (3, null)) with { ActualError = "Inverter history is unavailable." };
        var html = await RenderAsync(data, weatherFailed ? "history-weather-error" : "history-deye-error");

        Assert.Contains(weatherFailed ? "Weather history is unavailable." : "Inverter history is unavailable.", html);
        Assert.Contains("role=\"status\"", html);
        Assert.Equal("", Path(html, weatherFailed ? "possible-band" : "actual-series").Trim());
        Assert.Contains("M", Path(html, weatherFailed ? "actual-series" : "possible-band"));
        Assert.DoesNotContain("No data is available for this period yet", html);
    }

    [Theory]
    [InlineData(true, "The chart will appear after the first completed hour of the day.")]
    [InlineData(false, "No data is available for this period yet.")]
    public async Task MissingDataHasAnHonestEmptyState(bool noHours, string message)
    {
        var html = await RenderAsync(noHours ? Result() : Result((null, null), (null, null)), "history-empty-" + noHours);

        Assert.Contains(message, html);
        Assert.DoesNotContain("data-testid=\"possible-band\"", html);
        Assert.DoesNotContain("data-testid=\"actual-series\"", html);
    }

    [Fact]
    public async Task CoordinatesRemainInvariantUnderPolishCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            var html = await RenderAsync(RangeResult((1.3, 2.1, 2.8), (2.5, 3.3, 1.6), (4.2, 5.4, 3.1), (3.7, 4.9, 4.4), (0.8, 1.4, 1.2)));
            Assert.DoesNotContain(",", Path(html, "possible-band"));
            Assert.DoesNotContain(",", Path(html, "actual-series"));
            Assert.Contains("0.8–1.4 kW", html);
            Assert.Contains("actual 1.2 kW", html);
            Assert.NotEmpty(Vertices(Path(html, "possible-band")));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static SolarHistoryResult Result(params (double? PossibleLower, double? Actual)[] values) => RangeResult(
        values.Select(value => (value.PossibleLower, value.PossibleLower + 1, value.Actual)).ToArray());

    private static SolarHistoryResult RangeResult(params (double? Lower, double? Upper, double? Actual)[] values) => new(
        Start, Start.AddHours(values.Length), "Europe/Warsaw",
        values.Select((value, i) => new SolarHistoryPoint(Start.AddHours(i),
            value.Lower.HasValue ? new SolarHistoryPowerRange(value.Lower.Value, value.Upper!.Value) : null, value.Actual)).ToArray())
    { Today = Today, SelectedDate = Today };

    private static SolarHistoryResult ResultForDate(DateOnly date, DateOnly? today = null)
    {
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(6, 0)), TimeSpan.Zero);
        return new(start, start.AddHours(2), "Europe/Warsaw", [new(start, new(1, 2), 2), new(start.AddHours(1), new(3, 4), 4)])
        { Today = today ?? Today, SelectedDate = date };
    }

    private static bool DisabledButton(string html, string label)
    {
        var button = Regex.Match(html, "<button\\b[^>]*aria-label=\"" + Regex.Escape(label) + "\"[^>]*>").Value;
        Assert.NotEmpty(button);
        return Regex.IsMatch(button, "\\sdisabled(?:[\\s=>])");
    }

    private static string Path(string html, string id)
    {
        var tag = Regex.Match(html, "<path\\b[^>]*data-testid=\"" + id + "\"[^>]*/?>").Value;
        Assert.NotEmpty(tag);
        var path = Regex.Match(tag, "\\bd=\"([^\"]*)\"");
        Assert.True(path.Success, "SVG series has no path attribute.");
        return path.Groups[1].Value;
    }

    private static (double X, double Y)[] Vertices(string path) => Regex.Matches(path, "[ML]\\s+(-?\\d+(?:\\.\\d+)?)\\s+(-?\\d+(?:\\.\\d+)?)")
        .Select(match => (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)))
        .ToArray();

    private static async Task<string> RenderAsync(SolarHistoryResult data, string? scenario = null, bool overview = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NullJsRuntime>();
        services.AddSingleton(TimeProvider.System);
        var history = new UnusedHistoryService();
        services.AddSingleton<ISolarHistoryService>(history);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SolarHistoryChart>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Data"] = data, ["Overview"] = overview }));
            return output.ToHtmlString();
        });
        Assert.Equal(0, history.Calls);
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
                + "<body style='margin:0;padding:16px;background:#121212'><div style='margin:0 auto;max-width:1100px'>"
                + theme + html + "</div></body></html>";
            await File.WriteAllTextAsync(System.IO.Path.Combine(folder, scenario + ".html"), page);
        }
        var decoded = WebUtility.HtmlDecode(html);
        Assert.DoesNotMatch("[\\u0400-\\u04FF]", decoded);
        return decoded;
    }

    private static ServiceProvider InteractiveServices(ISolarHistoryService history, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NullJsRuntime>();
        services.AddSingleton(clock ?? new FixedClock());
        services.AddSingleton(history);
        return services.BuildServiceProvider();
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start.AddHours(5);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingHistoryService : ISolarHistoryService
    {
        public List<(SolarHistoryPeriod Period, DateOnly? Date, CancellationToken Token)> Calls { get; } = [];
        public bool HoldPastRequests { get; init; }
        public bool EmptyToday { get; init; }
        public bool FailFirstPastRequest { get; init; }
        public Func<DateOnly> DateProvider { get; init; } = () => Today;
        private readonly Dictionary<DateOnly, TaskCompletionSource<SolarHistoryResult>> _pending = [];
        private bool _pastRequestFailed;
        public Task<SolarHistoryResult> ReadAsync(SolarHistoryPeriod period, CancellationToken ct, DateOnly? endDate = null)
        {
            Calls.Add((period, endDate, ct));
            if (FailFirstPastRequest && endDate.HasValue && !_pastRequestFailed)
            {
                _pastRequestFailed = true;
                throw new InvalidOperationException("A transient history failure.");
            }
            if (EmptyToday && endDate is null) return Task.FromResult(ResultForDate(DateProvider(), DateProvider()) with { Points = [] });
            if (!HoldPastRequests || endDate is not { } date) return Task.FromResult(ResultForDate(endDate ?? DateProvider(), DateProvider()));
            // Deliberately return even after cancellation to verify that a late response cannot replace a newer day.
            var response = new TaskCompletionSource<SolarHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(date, response);
            return response.Task;
        }
        public void Complete(DateOnly date) => _pending[date].SetResult(ResultForDate(date));
    }

    // Framework rendering and event dispatch exercise the real component; no private methods or state are invoked.
    private sealed class EventRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();

        public async Task<int> MountAsync(bool overview = false)
        {
            var root = AssignRootComponentId(InstantiateComponent(typeof(SolarHistoryChart)));
            await RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?> { ["Overview"] = overview }));
            return root;
        }

        public Task ClickAsync(int root, string label)
        {
            var button = Button(root, label);
            Assert.False(button.Disabled, "Button is disabled: " + label);
            Assert.NotEqual(0UL, button.EventId);
            return DispatchEventAsync(button.EventId, null, new MouseEventArgs());
        }

        public record ButtonInfo(string Label, bool Disabled, ulong EventId);
        public ButtonInfo Button(int root, string label) => Assert.Single(Buttons(root), button => button.Label == label);

        private IEnumerable<ButtonInfo> Buttons(int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Component)
                    foreach (var button in Buttons(frame.ComponentId)) yield return button;
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                string? label = null;
                var disabled = false;
                var eventId = 0UL;
                for (var j = i + 1; j < i + frame.ElementSubtreeLength; j++)
                {
                    var child = frames.Array[j];
                    if (child.FrameType != RenderTreeFrameType.Attribute) continue;
                    if (child.AttributeName == "aria-label") label = child.AttributeValue?.ToString();
                    if (child.AttributeName == "disabled") disabled = child.AttributeValue is true;
                    if (child.AttributeName == "onclick") eventId = child.AttributeEventHandlerId;
                }
                label ??= string.Concat(frames.Array.Skip(i + 1).Take(frame.ElementSubtreeLength - 1).Select(child => child.FrameType switch
                {
                    RenderTreeFrameType.Text => child.TextContent,
                    RenderTreeFrameType.Markup => WebUtility.HtmlDecode(Regex.Replace(child.MarkupContent, "<[^>]*>", "")),
                    _ => ""
                }));
                yield return new(label, disabled, eventId);
            }
        }

        public string Text(int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            return string.Join(" ", frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
            {
                RenderTreeFrameType.Text => frame.TextContent,
                RenderTreeFrameType.Markup => frame.MarkupContent,
                RenderTreeFrameType.Component => Text(frame.ComponentId),
                _ => ""
            }));
        }
    }

    private sealed class UnusedHistoryService : ISolarHistoryService
    {
        public int Calls { get; private set; }
        public Task<SolarHistoryResult> ReadAsync(SolarHistoryPeriod period, CancellationToken ct, DateOnly? endDate = null)
        {
            Calls++;
            throw new InvalidOperationException("A provided result must not fetch another user's or period's data.");
        }
    }
    private sealed class NullJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}

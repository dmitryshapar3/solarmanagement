using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DeyeSolar.Web.Components.Charts;
using DeyeSolar.Web.Redesign;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace DeyeSolar.Web.Tests;

public sealed class ProductionForecastPlotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 8);

    [Fact]
    public async Task HourlyForecastHasItsOwnSeriesWithPointsInsideTheWeatherRangeAndNoActualLine()
    {
        var data = Hourly(2, 4, 1);
        var html = await Render(data, SolarHistoryPeriod.Today);
        var forecast = Path(html);
        var endpoints = Endpoints(forecast);

        Assert.Equal(3, endpoints.Length);
        Assert.Empty(Path(html, "actual-series"));
        Assert.NotEmpty(Path(html, "possible-band"));
        var max = Math.Ceiling(4 * 1.5 * 1.05);
        for (var index = 0; index < endpoints.Length; index++)
        {
            var hour = data.Hours[index];
            Assert.Equal(index * 848d / 2, endpoints[index].X, 3);
            Assert.InRange(endpoints[index].Y, 204 - hour.UpperKw!.Value / max * 204,
                204 - hour.LowerKw!.Value / max * 204);
            Assert.Equal(204 - hour.ExpectedKw!.Value / max * 204, endpoints[index].Y, 3);
        }
        Assert.Contains("kW", Readout(html));
        Assert.Contains(">Forecast<", Readout(html));
    }

    [Fact]
    public async Task DailyForecastPointsAlignWithDailyColumnsAndMissingDatesSplitTheSeries()
    {
        var data = Daily(10, 20, null, 30, 15);
        var html = await Render(data, SolarHistoryPeriod.Month);
        var path = Path(html);
        var endpoints = Endpoints(path);
        var step = 848d / 5;
        var pathTag = ForecastTag(html);

        Assert.Equal(2, Regex.Matches(path, "M ").Count);
        Assert.Equal(4, endpoints.Length);
        Assert.Contains("translate(84.8 0)", pathTag);
        int[] dateIndexes = [0, 1, 3, 4];
        var max = Math.Ceiling(30 * 1.5 * 1.05);
        for (var index = 0; index < endpoints.Length; index++)
        {
            var day = data.Days[dateIndexes[index]];
            Assert.Equal(dateIndexes[index] * step, endpoints[index].X, 3);
            Assert.InRange(endpoints[index].Y, 204 - day.UpperEnergyKwh!.Value / max * 204,
                204 - day.LowerEnergyKwh!.Value / max * 204);
        }
        Assert.Contains("kWh", Readout(html));
        Assert.Contains(">Forecast<", Readout(html));
        Assert.DoesNotContain("NaN", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FutureSelectionDisclosesMissingForecastWithoutInventingMeasuredZero(bool daily)
    {
        var html = await Render(daily ? Daily(10, null) : Hourly(2, null),
            daily ? SolarHistoryPeriod.Week : SolarHistoryPeriod.Today, selected: 1);

        Assert.Contains(">Forecast unavailable<", Readout(html));
        Assert.Contains("—", Readout(html));
        Assert.DoesNotContain("0.00", Readout(html));
        Assert.Equal(1, Endpoints(Path(html)).Length);
    }

    [Fact]
    public async Task AOneDayCustomWindowAndAnEmptyDailyWindowRemainRenderable()
    {
        var single = await Render(Daily(10), SolarHistoryPeriod.Custom);
        Assert.Contains(">Forecast<", Readout(single));
        Assert.Contains("10.00 kWh", Readout(single));
        Assert.DoesNotContain("NaN", single);

        var empty = await Render(Daily(), SolarHistoryPeriod.Custom);
        Assert.Contains("role=\"img\"", empty);
        Assert.DoesNotContain("NaN", empty);
    }

    private static ProductionViewDto Hourly(params double?[] values)
    {
        var start = Now.AddDays(1).Date.AddHours(6);
        var at = new DateTimeOffset(start, TimeSpan.Zero);
        return View(at, at.AddHours(values.Length), values.Select((value, index) => new ProductionHourDto(
            at.AddHours(index), null, null, 0, 3600, value, value * .5, value * 1.5, true)).ToArray(), []);
    }

    private static ProductionViewDto Daily(params double?[] values)
    {
        var at = new DateTimeOffset(Now.AddDays(1).Date, TimeSpan.Zero);
        return View(at, at.AddDays(values.Length), [], values.Select((value, index) => new ProductionDayDto(
            Today.AddDays(index + 1), null, 0, 0, value, value * .5, value * 1.5, true)).ToArray());
    }

    private static ProductionViewDto View(DateTimeOffset start, DateTimeOffset end,
        IReadOnlyList<ProductionHourDto> hours, IReadOnlyList<ProductionDayDto> days) => new(
        start, end, "UTC", DateOnly.FromDateTime(start.Date), Today, hours, days,
        null, null, 0, 0, null, null, null, null, null, null, Now, null, null, true);

    private static async Task<string> Render(ProductionViewDto data, SolarHistoryPeriod period, int selected = 0)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddComponentLocalization();
        services.AddSingleton<TimeProvider>(new FixedClock());
        services.AddSingleton<IJSRuntime, NullJs>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<ProductionPlot>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Data"] = data, ["Period"] = period,
                ["SelectedIndex"] = selected }))).ToHtmlString()));
    }

    private static string Readout(string html) => Regex.Match(html, "<div class=\"chart-readout\"[\\s\\S]*?(?=<div class=\"chart-legend\")").Value;
    private static string ForecastTag(string html) => Regex.Match(html, "<path\\b[^>]*data-testid=\"forecast-series\"[^>]*/?>").Value;
    private static string Path(string html, string id = "forecast-series")
    {
        var tag = Regex.Match(html, "<path\\b[^>]*data-testid=\"" + id + "\"[^>]*/?>").Value;
        Assert.NotEmpty(tag);
        return Regex.Match(tag, "\\bd=\"([^\"]*)\"").Groups[1].Value;
    }
    private static (double X, double Y)[] Endpoints(string path) => Regex.Matches(path, "([MC]) ([^MCZ]+)").Select(match =>
    {
        var numbers = match.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        return (numbers[^2], numbers[^1]);
    }).ToArray();
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NullJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}

public sealed class ProductionRequestParsingTests
{
    [Theory]
    [InlineData(EnergyPeriod.Day)]
    [InlineData(EnergyPeriod.Week)]
    [InlineData(EnergyPeriod.RollingMonth)]
    [InlineData(EnergyPeriod.CalendarMonth)]
    [InlineData(EnergyPeriod.Custom)]
    public void ProductionCsvEnumRequestsRoundTripEverySharedEnergyWindow(EnergyPeriod period)
    {
        var date = new DateOnly(2026, 10, 8);
        var selection = new EnergyPeriodSelection(period, date,
            period == EnergyPeriod.Custom ? date : null, period == EnergyPeriod.Custom ? date.AddDays(9) : null);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new("?period=" + selection.ProductionPeriod + "&date=2026-10-08"
            + (period == EnergyPeriod.Custom ? "&from=2026-10-08&through=2026-10-17" : ""));

        Assert.Equal(selection.ToProductionRequest(), RedesignEndpoints.ParseProductionRequest(context));
    }

    [Theory]
    [InlineData("Month", SolarHistoryPeriod.Month)]
    [InlineData("month", SolarHistoryPeriod.Month)]
    [InlineData("MONTH", SolarHistoryPeriod.Month)]
    [InlineData("CalendarMonth", SolarHistoryPeriod.CalendarMonth)]
    [InlineData("calendarmonth", SolarHistoryPeriod.CalendarMonth)]
    [InlineData("day", SolarHistoryPeriod.Today)]
    [InlineData("7d", SolarHistoryPeriod.Week)]
    [InlineData("30d", SolarHistoryPeriod.Month)]
    public void LegacyPeriodNamesAndShortAliasesKeepTheirDefinedMeaning(string token, SolarHistoryPeriod period)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new("?period=" + token);
        Assert.Equal(period, RedesignEndpoints.ParseProductionRequest(context).Period);
    }

    [Theory]
    [InlineData("period=unknown")]
    [InlineData("period=999")]
    [InlineData("period=day&date=2026-02-30")]
    [InlineData("period=custom&from=invalid&through=2026-10-08")]
    public void MalformedRequestsFailDuringParsing(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new("?" + query);
        Assert.Throws<ArgumentException>(() => RedesignEndpoints.ParseProductionRequest(context));
    }
}

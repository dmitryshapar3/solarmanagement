using System.Net;
using System.Text.RegularExpressions;
using DeyeSolar.Web.Components.Ui;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeyeSolar.Web.Tests;

public class EnergyPeriodSelectionTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);

    [Theory]
    [InlineData(EnergyPeriod.Day, "day", 1)]
    [InlineData(EnergyPeriod.Week, "7d", 7)]
    [InlineData(EnergyPeriod.RollingMonth, "30d", 30)]
    [InlineData(EnergyPeriod.CalendarMonth, "month", 31)]
    [InlineData(EnergyPeriod.Custom, "custom", 12)]
    public void SwitchingEnergyTabsPreservesEverySelectedCalendarWindow(EnergyPeriod period, string queryPeriod, int days)
    {
        var selected = new EnergyPeriodSelection(period, Today,
            period == EnergyPeriod.Custom ? Today : null,
            period == EnergyPeriod.Custom ? Today.AddDays(11) : null);
        var production = new Uri("https://local.invalid" + selected.Url("/energy"));
        var export = new Uri("https://local.invalid" + selected.Url("/energy/export"));
        Assert.Equal(production.Query, export.Query);
        var query = QueryHelpers.ParseQuery(export.Query);
        Assert.Equal(queryPeriod, query["period"].ToString());
        Assert.True(EnergyPeriodSelection.TryParse(query["period"].ToString(), query["date"].ToString(),
            query.ContainsKey("from") ? query["from"].ToString() : null,
            query.ContainsKey("through") ? query["through"].ToString() : null, Today, out var roundTrip));
        Assert.Equal(selected, roundTrip);
        Assert.Equal(days, roundTrip.LastDate.DayNumber - roundTrip.FirstDate.DayNumber + 1);
        var sales = roundTrip.ToSalesRequest();
        Assert.True(sales.AllowFuture);
        Assert.Equal(roundTrip.Date, sales.Date);
        var range = ExportSalesRange.Create(sales, new() { ContractStartDate = new(2024, 1, 1) },
            new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        Assert.Equal(roundTrip.FirstDate, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(range.Start, zone).DateTime));
        Assert.Equal(roundTrip.LastDate.AddDays(1), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(range.End, zone).DateTime));
    }

    [Fact]
    public void CalendarMonthsAndMonthNavigationKeepLeapDayAndCompleteMonthBounds()
    {
        var january = new EnergyPeriodSelection(EnergyPeriod.CalendarMonth, new(2028, 1, 31));
        var february = january.Move(1);
        Assert.Equal(new DateOnly(2028, 2, 1), february.FirstDate);
        Assert.Equal(new DateOnly(2028, 2, 29), february.LastDate);
        Assert.Equal(29, february.LastDate.DayNumber - february.FirstDate.DayNumber + 1);
        Assert.Equal(new DateOnly(2028, 3, 31), february.Move(1).LastDate);
        Assert.Equal(new DateOnly(2027, 2, 28), new EnergyPeriodSelection(EnergyPeriod.CalendarMonth, new(2027, 2, 1)).LastDate);
    }

    [Theory]
    [InlineData(EnergyPeriod.Week, 7)]
    [InlineData(EnergyPeriod.RollingMonth, 30)]
    public void ForwardNavigationMovesOneCompleteWindowIntoForecastDates(EnergyPeriod period, int days)
    {
        var initial = new EnergyPeriodSelection(period, Today);
        var next = initial.Move(1);
        Assert.Equal(Today.AddDays(days), next.Date);
        Assert.Equal(Today.AddDays(1), next.FirstDate);
        Assert.Equal(days, next.LastDate.DayNumber - next.FirstDate.DayNumber + 1);
        Assert.True(next.IsValid(Today));
        Assert.Equal(initial, next.Move(-1));
    }

    [Fact]
    public void CustomNavigationMovesTheInclusiveRangeWithoutChangingItsLength()
    {
        var initial = new EnergyPeriodSelection(EnergyPeriod.Custom, Today, Today, Today.AddDays(13));
        var next = initial.Move(1);
        Assert.Equal(Today.AddDays(14), next.FirstDate);
        Assert.Equal(Today.AddDays(27), next.LastDate);
        Assert.Equal(initial, next.Move(-1));
    }

    [Fact]
    public void CustomRangeAllows366InclusiveDaysAndRejectsReverseOrLargerWindows()
    {
        var first = Today.AddDays(-365).ToString("yyyy-MM-dd");
        Assert.True(EnergyPeriodSelection.TryParse("custom", Today.ToString("yyyy-MM-dd"), first,
            Today.ToString("yyyy-MM-dd"), Today, out var maximum));
        Assert.Equal(366, maximum.LastDate.DayNumber - maximum.FirstDate.DayNumber + 1);
        Assert.False(EnergyPeriodSelection.TryParse("custom", null, Today.AddDays(-366).ToString("yyyy-MM-dd"),
            Today.ToString("yyyy-MM-dd"), Today, out _));
        Assert.False(EnergyPeriodSelection.TryParse("custom", null, Today.ToString("yyyy-MM-dd"),
            Today.AddDays(-1).ToString("yyyy-MM-dd"), Today, out _));
    }

    [Theory]
    [InlineData("unknown", "2026-10-08", null, null)]
    [InlineData("day", "2026-02-30", null, null)]
    [InlineData("7d", "2000-01-05", null, null)]
    [InlineData("30d", "2000-01-29", null, null)]
    [InlineData("custom", "2026-10-08", null, null)]
    [InlineData("custom", "2026-10-08", "2026-10-08", "2027-10-10")]
    public void InvalidPeriodsDatesAndRangesCannotBecomeSharedSelections(string period, string date, string? from, string? through)
        => Assert.False(EnergyPeriodSelection.TryParse(period, date, from, through, Today, out _));

    [Fact]
    public void FutureSelectionIsBoundedByTheLastDisplayedDay()
    {
        var latest = Today.AddDays(366);
        Assert.True(new EnergyPeriodSelection(EnergyPeriod.Day, latest).IsValid(Today));
        Assert.False(new EnergyPeriodSelection(EnergyPeriod.Day, latest.AddDays(1)).IsValid(Today));
        Assert.False(new EnergyPeriodSelection(EnergyPeriod.CalendarMonth, latest).IsValid(Today));
    }

    [Theory]
    [InlineData(EnergyPeriod.Day)]
    [InlineData(EnergyPeriod.Week)]
    [InlineData(EnergyPeriod.RollingMonth)]
    [InlineData(EnergyPeriod.CalendarMonth)]
    [InlineData(EnergyPeriod.Custom)]
    public async Task SharedFilterComponentAlwaysRendersAllFiveModesAndFutureShortcuts(EnergyPeriod period)
    {
        var selected = new EnergyPeriodSelection(period, Today,
            period == EnergyPeriod.Custom ? Today.AddDays(-2) : null,
            period == EnergyPeriod.Custom ? Today : null);
        await using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<EnergyPeriodFilters>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Selection"] = selected, ["Today"] = Today }))).ToHtmlString()));
        Assert.Contains("data-testid=\"energy-period-filters\"", html);
        foreach (var label in new[] { "Day", "7 days", "30 days", "Month", "Custom", "Today", "Next 7 days", "Next 30 days" })
            Assert.Contains(">" + label + "</button>", html);
        Assert.Equal(1, Regex.Matches(html, "aria-pressed=\"true\"").Count);
        Assert.Equal(4, Regex.Matches(html, "aria-pressed=\"false\"").Count);
        Assert.Contains("aria-label=\"Next period\"", html);
        Assert.DoesNotContain("disabled", html);
        if (period == EnergyPeriod.CalendarMonth) Assert.Contains("type=\"month\"", html);
        if (period == EnergyPeriod.Custom)
        {
            Assert.Contains("Inclusive · up to 366 days", html);
            Assert.Equal(2, Regex.Matches(html, "type=\"date\"").Count);
        }
        else Assert.Contains("value=\"" + Today.ToString(period == EnergyPeriod.CalendarMonth ? "yyyy-MM" : "yyyy-MM-dd") + "\"", html);
    }

    [Theory]
    [InlineData("Next 7 days", EnergyPeriod.Week, 7)]
    [InlineData("Next 30 days", EnergyPeriod.RollingMonth, 30)]
    public async Task ForecastShortcutSelectsTodayThroughTheRequestedNumberOfDays(string label, EnergyPeriod period, int days)
    {
        await using var services = Services();
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            EnergyPeriodSelection? changed = null;
            var callback = EventCallback.Factory.Create<EnergyPeriodSelection>(new object(), (EnergyPeriodSelection value) => changed = value);
            var root = await renderer.MountAsync(new(EnergyPeriod.Day, Today), callback);
            await renderer.ClickAsync(root, label);
            Assert.NotNull(changed);
            Assert.Equal(period, changed.Period);
            Assert.Equal(Today, changed.FirstDate);
            Assert.Equal(Today.AddDays(days - 1), changed.LastDate);
        });
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddComponentLocalization();
        return services.BuildServiceProvider();
    }

    private sealed class EventRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        public async Task<int> MountAsync(EnergyPeriodSelection selection, EventCallback<EnergyPeriodSelection> callback)
        {
            var root = AssignRootComponentId(InstantiateComponent(typeof(EnergyPeriodFilters)));
            await RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Selection"] = selection, ["Today"] = Today, ["SelectionChanged"] = callback }));
            return root;
        }
        public Task ClickAsync(int root, string label)
        {
            var frames = GetCurrentRenderTreeFrames(root);
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                var children = frames.Array.Skip(index + 1).Take(frame.ElementSubtreeLength - 1).ToArray();
                var text = string.Concat(children.Where(child => child.FrameType == RenderTreeFrameType.Text).Select(child => child.TextContent));
                if (text != label) continue;
                var click = Assert.Single(children, child => child.FrameType == RenderTreeFrameType.Attribute && child.AttributeName == "onclick");
                return DispatchEventAsync(click.AttributeEventHandlerId, null, new MouseEventArgs());
            }
            throw new InvalidOperationException("Button not rendered: " + label);
        }
    }
}

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace DeyeSolar.Web.Tests;

public class SalesStatisticsTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 22, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("normal")]
    [InlineData("zero")]
    [InlineData("partial")]
    [InlineData("error")]
    [InlineData("empty")]
    [InlineData("before-contract")]
    [InlineData("month")]
    [InlineData("year")]
    [InlineData("custom")]
    public async Task SalesInterfaceUsesEnglishForVisibleAndAccessibleText(string scenario)
    {
        var empty = Result() with
        {
            ExpectedHours = 0, ObservedHours = 0, ValuedHours = 0, Buckets = [],
            ExportKwh = null, CreditedExportKwh = null, EnergyValuePln = null, EstimatedDepositPln = null
        };
        var data = scenario switch
        {
            "normal" => Result(),
            "zero" => Result() with
            {
                ExportKwh = 0, CreditedExportKwh = 0, EnergyValuePln = 0, EstimatedDepositPln = 0,
                Buckets = [new(Start, Start.AddHours(1), 0, 0, 0, 0, 1, 1, 1)]
            },
            "partial" => Result() with
            {
                ExpectedHours = 2, EnergyValuePln = null, EstimatedDepositPln = null, ValuedHours = 0,
                PriceError = "The price has not been published yet.",
                Buckets = [new(Start, Start.AddHours(1), 5, 3, null, null, 1, 1, 0),
                    new(Start.AddHours(1), Start.AddHours(2), null, null, null, null, 1, 0, 0)]
            },
            "error" => empty with { DataError = "Deye history is unavailable." },
            "empty" => empty,
            "before-contract" => empty with { Request = new(ExportSalesPeriod.Day, Today.AddDays(-3)) },
            "month" => Result(new(ExportSalesPeriod.Month, new(2026, 9, 1))),
            "year" => Result(new(ExportSalesPeriod.Year, new(2026, 1, 1))),
            "custom" => Result(new(ExportSalesPeriod.Custom, Today, Today.AddDays(-2), Today)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
            var html = await RenderAsync(data);

            // Decode the full HTML so translated accessibility text and SVG tooltips are checked too.
            Assert.DoesNotMatch("[\\u0400-\\u04ff]", html);
            Assert.Contains("Electricity sales", html);
            Assert.Contains("Deye estimate", html);
            Assert.Contains("aria-label=\"Refresh sales\"", html);
            Assert.Contains("aria-label=\"Sales period\"", html);
            Assert.Contains("Exported to grid", html);
            Assert.Contains("Energy value", html);
            Assert.Contains("Estimated deposit", html);
            Assert.Contains("kWh", html);
            foreach (var period in new[] { "Day", "Month", "Year", "Custom" })
                Assert.Matches($">{period}</button>", html);
            var caption = scenario switch
            {
                "before-contract" => "27 September 2026",
                "month" => "September 2026",
                "year" => "2026",
                "custom" => "28 September 2026 – 30 September 2026",
                _ => "30 September 2026"
            };
            Assert.Matches($"data-testid=\"sales-period\"[^>]*>{Regex.Escape(caption)}</span>", html);
            if (scenario == "normal")
            {
                Assert.Matches("data-testid=\"sales-value\"[^>]*>1[.]20<small[^>]*>PLN", html);
                Assert.Contains("exported 5.00 kWh, after hourly netting 3.00 kWh, value 1.20 PLN", html);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public async Task OverviewKeepsTodayEnergyAndItsProvisionalChartWithoutDetailedPageControls()
    {
        var data = Result() with
        {
            Buckets = [new(Start, Start.AddHours(1), 5, 3, 1.2m, 1.476m, 1, 1, 1),
                new(Start.AddHours(1), Start.AddHours(2), null, null, null, null, 0, 0, 0)],
            CurrentHour = new(Start.AddHours(1), Start.AddHours(1).AddMinutes(15), .25m, .20m, .1m, .123m, 900),
            UpdatedAt = Start.AddHours(1).AddMinutes(16)
        };
        var html = await RenderAsync(data, overview: true);

        Assert.Matches("<h2[^>]*id=\"sales-title\"[^>]*>Electricity sales</h2>", html);
        Assert.Contains("href=\"/sales-details?period=Day&date=2026-09-30&returnTo=%2Fsales\"", html);
        Assert.Matches("<a[^>]*aria-label=\"Electricity sales details\"[^>]*>Details</a>", html);
        Assert.Contains("Exported today", html);
        Assert.Contains("Energy value", html);
        Assert.Equal(2, Regex.Matches(html, "data-testid=\"sales-(?:export|value|deposit)\"").Count);
        Assert.DoesNotContain("Estimated deposit", html);
        Assert.DoesNotContain("aria-label=\"Sales period\"", html);
        Assert.DoesNotContain("aria-label=\"Date\"", html);
        Assert.DoesNotContain("aria-label=\"Previous period\"", html);
        Assert.DoesNotContain("How the estimate is calculated", html);
        Assert.DoesNotMatch("<div[^>]*class=\"sales-coverage\"[^>]*>", html);
        Assert.Contains("Deye estimate", html);
        Assert.Contains("data-testid=\"sales-progress-bar\"", html);
        Assert.Contains("Current hour · in progress", html);
        Assert.Contains("Totals cover completed hours. Current hour is provisional.", html);
        Assert.DoesNotMatch("[\\u0400-\\u04ff]", html);
    }

    [Fact]
    public async Task OverviewRetainsCriticalPartialAndSourceWarnings()
    {
        var data = Result() with
        {
            ExpectedHours = 2,
            DataError = "Some Deye readings are missing.", PriceError = "Some prices are not available."
        };
        var html = await RenderAsync(data, overview: true);
        Assert.Contains(data.DataError, html);
        Assert.Contains(data.PriceError, html);
        Assert.Contains("Partial data · totals for available hours", html);
        Assert.DoesNotContain("Readings 1 of 2 h", html);
        Assert.DoesNotMatch("[\\u0400-\\u04ff]", html);
    }

    [Fact]
    public async Task DetailedSalesLeadsWithTheChartAndReportsCompletedAndCurrentHoursSeparately()
    {
        var data = Result() with { CurrentHour = new(Start, Start.AddMinutes(15), .25m, .20m, null, null, 900) };
        var html = await RenderAsync(data, detailed: true);
        var plot = html.IndexOf("class=\"sales-plot\"", StringComparison.Ordinal);
        var totals = html.IndexOf("class=\"sales-totals\"", StringComparison.Ordinal);
        Assert.True(plot >= 0 && plot < totals);
        Assert.Contains("aria-label=\"Sales period\"", html);
        Assert.Contains("aria-label=\"Chart metric\"", html);
        Assert.Contains("Data coverage", html);
        Assert.Contains("Contract calculation", html);
        Assert.Contains("Completed interval breakdown", html);
        Assert.Contains("Provisional credited export", html);
        Assert.Contains("Provisional value", html);
        Assert.Matches("Provisional value[\\s\\S]*?<dd[^>]*>— PLN</dd>", html);
        Assert.Matches("data-testid=\"sales-export\"[^>]*>5[.]00<small", html);
        Assert.Matches("<td[^>]*>5[.]00</td>", html);
        Assert.DoesNotContain("aria-label=\"Electricity sales details\"", html);
    }

    [Fact]
    public async Task SalesDetailsLinkRetainsTheSelectedCustomWindow()
    {
        var html = await RenderAsync(Result(new(ExportSalesPeriod.Custom, Today, Today.AddDays(-2), Today)));
        Assert.Contains("href=\"/sales-details?period=Custom&date=2026-09-30&from=2026-09-28&through=2026-09-30&returnTo=%2Fsales\"", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticRefreshKeepsTheGraphAndLatestUserCursorWhileItsRequestIsPending(bool overview)
    {
        var clock = new ManualClock(new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var history = new SalesService { ResultFactory = request => MultipleHours(request) };
        await using var services = Services(history, clock);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync(overview: overview);
            await renderer.ClickAsync(root, "Previous interval");
            Assert.Equal(Start.AddHours(1).ToString("O"), Assert.Single(renderer.Attributes(root, "datetime")));
            history.HoldNext = true;
            clock.Advance(TimeSpan.FromMinutes(5));
            await renderer.WaitForAsync(() => history.Calls.Count == 2);
            Assert.Contains("Refreshing…", renderer.Text(root));
            Assert.Equal(3, renderer.Attributes(root, "data-testid").Count(value => value == "sales-bar"));
            Assert.Equal(Start.AddHours(1).ToString("O"), Assert.Single(renderer.Attributes(root, "datetime")));
            await renderer.ClickAsync(root, "Previous interval");
            history.Complete(Today);
            await renderer.WaitForAsync(() => !renderer.Control(root, "Refresh sales").Disabled);
            Assert.Equal(Start.ToString("O"), Assert.Single(renderer.Attributes(root, "datetime")));
            Assert.False(renderer.Control(root, "Next interval").Disabled);
            Assert.Equal(2, history.Calls.Count);
        });
    }

    [Fact]
    public async Task AutomaticFailureKeepsPreviousValuesAndTheirTimestampWithAnExplicitStaleWarning()
    {
        var clock = new ManualClock(new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var updatedAt = Start.AddHours(2);
        var history = new SalesService { ResultFactory = request => MultipleHours(request) with { UpdatedAt = updatedAt } };
        await using var services = Services(history, clock);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            await renderer.ClickAsync(root, "Previous interval");
            history.FailNext = true;
            clock.Advance(TimeSpan.FromMinutes(5));
            await renderer.WaitForAsync(() => renderer.Text(root).Contains("Refresh failed", StringComparison.Ordinal));
            Assert.Contains("Refresh failed. Showing previous data; values may be out of date.", renderer.Text(root));
            Assert.Contains("6.00", renderer.Text(root));
            Assert.Contains(updatedAt.ToString("O"), renderer.Attributes(root, "datetime"));
            Assert.Contains(Start.AddHours(1).ToString("O"), renderer.Attributes(root, "datetime"));
            Assert.Equal(3, renderer.Attributes(root, "data-testid").Count(value => value == "sales-bar"));
            await renderer.ClickAsync(root, "Retry");
            Assert.DoesNotContain("Refresh failed", renderer.Text(root));
            Assert.Equal(3, history.Calls.Count);
        });
    }

    [Fact]
    public async Task AnAuthoritativeEmptyRefreshReplacesPreviousValuesRatherThanRetainingThemAsTrusted()
    {
        var clock = new ManualClock(new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var changed = false;
        var history = new SalesService
        {
            ResultFactory = request => changed ? Result(request) with
            {
                ExportKwh = null, CreditedExportKwh = null, EnergyValuePln = null, EstimatedDepositPln = null,
                ExpectedHours = 1, ObservedHours = 0, ValuedHours = 0, Buckets = [],
                DataError = "Installation settings changed. Refresh the page."
            } : Result(request)
        };
        await using var services = Services(history, clock);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            changed = true;
            clock.Advance(TimeSpan.FromMinutes(5));
            await renderer.WaitForAsync(() => renderer.Text(root).Contains("Installation settings changed", StringComparison.Ordinal));
            Assert.Contains("Installation settings changed", renderer.Text(root));
            Assert.DoesNotContain("5.00", renderer.Text(root));
            Assert.DoesNotContain("sales-bar", renderer.Attributes(root, "data-testid"));
            Assert.DoesNotContain("Refresh failed", renderer.Text(root));
        });
    }

    [Fact]
    public async Task AutomaticRefreshRecoversAfterAnInitialFailure()
    {
        var clock = new ManualClock(new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var history = new SalesService { FailNext = true };
        await using var services = Services(history, clock);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            Assert.Contains("Sales could not be loaded", renderer.Text(root));
            clock.Advance(TimeSpan.FromMinutes(5));
            await renderer.WaitForAsync(() => history.Calls.Count == 2 && !renderer.Control(root, "Refresh sales").Disabled);
            Assert.DoesNotContain("Refresh failed", renderer.Text(root));
            Assert.Contains("5.00", renderer.Text(root));
            Assert.Equal(1, clock.ActiveTimers);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnHourBoundaryFollowsTheNewCurrentHourUnlessTheUserPinnedAnInterval(bool pinned)
    {
        var clock = new ManualClock(Start.AddHours(1).AddMinutes(58));
        var advanced = false;
        var history = new SalesService
        {
            ResultFactory = request => Result(request) with
            {
                End = Start.AddDays(1), ExportKwh = advanced ? 5.5m : 5,
                ExpectedHours = advanced ? 2 : 1, ObservedHours = advanced ? 2 : 1, ValuedHours = advanced ? 2 : 1,
                Buckets = [new(Start, Start.AddHours(1), 5, 3, 1.2m, 1.476m, 1, 1, 1),
                    advanced ? new(Start.AddHours(1), Start.AddHours(2), .5m, .4m, .2m, .246m, 1, 1, 1)
                        : new(Start.AddHours(1), Start.AddHours(2), null, null, null, null, 0, 0, 0),
                    new(Start.AddHours(2), Start.AddHours(3), null, null, null, null, 0, 0, 0)],
                CurrentHour = advanced ? new(Start.AddHours(2), Start.AddHours(2).AddMinutes(2), .1m, .1m, .04m, .0492m, 120)
                    : new(Start.AddHours(1), Start.AddHours(1).AddMinutes(57), .45m, .35m, .175m, .21525m, 3420)
            }
        };
        await using var services = Services(history, clock);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            if (pinned)
            {
                await renderer.ClickAsync(root, "Previous interval");
                await renderer.ClickAsync(root, "Next interval");
            }
            advanced = true;
            clock.Advance(TimeSpan.FromMinutes(5));
            await renderer.WaitForAsync(() => history.Calls.Count == 2 && !renderer.Control(root, "Refresh sales").Disabled);
            Assert.Equal(Start.AddHours(pinned ? 1 : 2).ToString("O"), Assert.Single(renderer.Attributes(root, "datetime")));
            Assert.Equal(2, renderer.Attributes(root, "data-testid").Count(value => value == "sales-bar"));
            Assert.Equal(1, renderer.Attributes(root, "data-testid").Count(value => value == "sales-progress-bar"));
            Assert.Equal(pinned ? 0 : 1, renderer.Attributes(root, "data-testid").Count(value => value == "sales-current-hour"));
            Assert.Contains("5.50", renderer.Text(root));
        });
    }

    [Fact]
    public async Task OverviewMidnightRefreshKeepsTheOldDateUntilReplacementAndResetsTheOldCursor()
    {
        var clock = new ManualClock(new(2026, 9, 30, 21, 58, 0, TimeSpan.Zero));
        var history = new SalesService { ResultFactory = request => MultipleHours(request) };
        await using var services = Services(history, clock);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync(overview: true);
            await renderer.ClickAsync(root, "Previous interval");
            history.HoldNext = true;
            clock.Advance(TimeSpan.FromMinutes(5));
            await renderer.WaitForAsync(() => history.Calls.Count == 2);
            Assert.Contains("30 September 2026", renderer.Text(root));
            Assert.Contains("Exported on", renderer.Text(root));
            Assert.DoesNotContain("Exported today", renderer.Text(root));
            Assert.DoesNotContain("Today by hour", renderer.Text(root));
            history.Complete(Today.AddDays(1));
            await renderer.WaitForAsync(() => !renderer.Control(root, "Refresh sales").Disabled);
            Assert.Contains("1 October 2026", renderer.Text(root));
            Assert.Contains("Exported today", renderer.Text(root));
            Assert.Contains("Today by hour", renderer.Text(root));
            Assert.True(renderer.Control(root, "Next interval").Disabled);
            Assert.Equal(Start.AddDays(1).AddHours(2).ToString("O"), Assert.Single(renderer.Attributes(root, "datetime")));
        });
    }

    [Fact]
    public async Task CurrentHourShowsMeasuredEnergyWithoutAddingItToCompletedTotalsOrProjectingIt()
    {
        var data = Result() with
        {
            ExpectedHours = 0, ObservedHours = 0, ValuedHours = 0,
            ExportKwh = null, CreditedExportKwh = null, EnergyValuePln = null, EstimatedDepositPln = null,
            Buckets = [new(Start, Start.AddHours(1), null, null, null, null, 0, 0, 0),
                new(Start.AddHours(1), Start.AddHours(2), null, null, null, null, 0, 0, 0)],
            CurrentHour = new(Start, Start.AddMinutes(15), .25m, .20m, .1m, .123m, 900),
            UpdatedAt = Start.AddMinutes(16)
        };
        var html = await RenderAsync(data);

        Assert.Equal(3, Regex.Matches(html, "data-testid=\"sales-(?:export|value|deposit)\"[^>]*>—").Count);
        Assert.DoesNotContain("data-testid=\"sales-bar\"", html);
        var bar = Assert.Single(Regex.Matches(html, "<rect[^>]*data-testid=\"sales-progress-bar\"[^>]*>")).Value;
        Assert.Contains("y=\"181\"", bar);
        Assert.Contains("height=\"51\"", bar);
        Assert.Contains("Current hour · in progress", html);
        Assert.Contains("Measured through 30 September 00:15 (+02:00)", html);
        Assert.Contains("exported so far 0.25 kWh", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Updated", html);
        Assert.Contains("00:16 (+02:00)", html);
        Assert.Contains("Totals cover completed hours. Current hour is provisional.", html);
        Assert.Contains("no projection to the end of the hour", html);
        Assert.DoesNotContain("There are no completed hours", html);
        Assert.DoesNotMatch("[\\u0400-\\u04ff]", html);
    }

    [Fact]
    public async Task MeasuredCurrentHourZeroHasABaselineMarkerButMissingCurrentHourRemainsUnknown()
    {
        var data = Result() with
        {
            ExpectedHours = 0, ObservedHours = 0, ValuedHours = 0,
            ExportKwh = null, CreditedExportKwh = null, EnergyValuePln = null, EstimatedDepositPln = null,
            Buckets = [new(Start, Start.AddHours(1), null, null, null, null, 0, 0, 0)],
            CurrentHour = new(Start, Start.AddMinutes(15), 0, 0, null, null, 900)
        };
        var zero = await RenderAsync(data);
        var bar = Assert.Single(Regex.Matches(zero, "<rect[^>]*data-testid=\"sales-progress-bar\"[^>]*>")).Value;
        Assert.Contains("y=\"232\"", bar);
        Assert.Contains("height=\"1.5\"", bar);
        Assert.Contains("Awaiting current-hour prices.", zero);
        Assert.Contains("provisional value — PLN", zero);

        var missing = await RenderAsync(data with { CurrentHour = new(Start, null, null, null, null, null, 0) });
        Assert.DoesNotContain("data-testid=\"sales-progress-bar\"", missing);
        Assert.Contains("Awaiting current-hour readings.", missing);
        Assert.DoesNotContain("0.00 kWh", missing);
        Assert.DoesNotContain("0.00 PLN", missing);
    }

    [Theory]
    [InlineData(ExportSalesPeriod.Month)]
    [InlineData(ExportSalesPeriod.Year)]
    [InlineData(ExportSalesPeriod.Custom)]
    public async Task CalendarBucketsDistinguishTheCurrentIncrementAndScaleForBothAmounts(ExportSalesPeriod period)
    {
        var data = Result(new(period, Today, period == ExportSalesPeriod.Custom ? Today : null, period == ExportSalesPeriod.Custom ? Today : null)) with
        {
            ExportKwh = 2, CreditedExportKwh = 2, EnergyValuePln = 1, EstimatedDepositPln = 1.23m,
            Buckets = [new(Start, Start.AddDays(1), 2, 2, 1, 1.23m, 1, 1, 1)],
            CurrentHour = new(Start.AddHours(1), Start.AddHours(1).AddMinutes(30), 2, 1.5m, .75m, .9225m, 1800)
        };
        var html = await RenderAsync(data);

        Assert.Matches("data-testid=\"sales-export\"[^>]*>2[.]00<small", html);
        var completed = Assert.Single(Regex.Matches(html, "<rect[^>]*data-testid=\"sales-bar\"[^>]*>")).Value;
        var progress = Assert.Single(Regex.Matches(html, "<rect[^>]*data-testid=\"sales-progress-bar\"[^>]*>")).Value;
        Assert.Contains("y=\"130\"", completed);
        Assert.Contains("height=\"102\"", completed);
        Assert.Contains("y=\"28\"", progress);
        Assert.Contains("height=\"102\"", progress);
        Assert.Contains("sales-progress-bar", progress);
        Assert.Contains("Completed export", html);
        Assert.Contains("Exported so far", html);
        Assert.Contains("Completed hours only. Current hour · in progress", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CurrentHourMoneyUsesItsOwnKnownValueAndNeverReplacesMissingPricesWithZero(bool priceKnown)
    {
        var history = new SalesService
        {
            ResultFactory = request => Result(request) with
            {
                ExpectedHours = 0, ObservedHours = 0, ValuedHours = 0,
                ExportKwh = null, CreditedExportKwh = null, EnergyValuePln = null, EstimatedDepositPln = null,
                Buckets = [new(Start, Start.AddHours(1), null, null, null, null, 0, 0, 0)],
                CurrentHour = new(Start, Start.AddMinutes(20), .5m, .4m, priceKnown ? -.2m : null, priceKnown ? -.246m : null, 1200)
            }
        };
        await using var services = Services(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            await renderer.ClickAsync(root, "Value");
            Assert.Contains(priceKnown ? "-0.20 PLN" : "Awaiting current-hour prices.", renderer.Text(root));
            Assert.Equal(priceKnown ? 1 : 0, renderer.Attributes(root, "data-testid").Count(value => value == "sales-progress-bar"));
            Assert.DoesNotContain("0.00 PLN", renderer.Text(root));
            Assert.DoesNotMatch("[\\u0400-\\u04ff]", renderer.Text(root));
            Assert.Single(history.Calls);
        });
    }

    [Fact]
    public async Task AutomaticRefreshRunsEveryFiveMinutesAndKeepsTheHistoricalSelection()
    {
        var clock = new ManualClock(new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var history = new SalesService();
        await using var services = Services(history, clock);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            Assert.Equal(1, clock.ActiveTimers);
            clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
            await Task.Yield();
            Assert.Single(history.Calls);
            clock.Advance(TimeSpan.FromSeconds(1));
            await renderer.WaitForAsync(() => history.Calls.Count == 2);
            Assert.Equal(2, history.Calls.Count);
            await renderer.ClickAsync(root, "Previous period");
            var historical = history.Calls[^1].Request;
            clock.Advance(TimeSpan.FromMinutes(5));
            await renderer.WaitForAsync(() => history.Calls.Count == 4);
            Assert.Equal(4, history.Calls.Count);
            Assert.Equal(historical, history.Calls[^1].Request);
            Assert.False(history.Calls[^1].Token.IsCancellationRequested);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticRefreshAtWarsawMidnightFollowsTodayOnlyForAnUnpinnedSelection(bool historical)
    {
        var clock = new ManualClock(new(2026, 9, 30, 21, 58, 0, TimeSpan.Zero));
        var history = new SalesService();
        await using var services = Services(history, clock);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            if (historical) await renderer.ClickAsync(root, "Previous period");
            clock.Advance(TimeSpan.FromMinutes(5));
            await renderer.WaitForAsync(() => history.Calls.Count == (historical ? 3 : 2));
            Assert.Equal(historical ? Today.AddDays(-1) : Today.AddDays(1), history.Calls[^1].Request.Date);
            Assert.Contains(historical ? "29 September 2026" : "1 October 2026", renderer.Text(root));
        });
    }

    [Fact]
    public async Task AutomaticRefreshDoesNotCancelOrDuplicateAnActiveUserRequest()
    {
        var clock = new ManualClock(new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var history = new SalesService { HoldHistorical = true };
        await using var services = Services(history, clock);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            var pending = renderer.ClickAsync(root, "Previous period");
            clock.Advance(TimeSpan.FromMinutes(5));
            await Task.Yield();
            Assert.Equal(2, history.Calls.Count);
            Assert.False(history.Calls[1].Token.IsCancellationRequested);
            history.Complete(Today.AddDays(-1));
            await pending;
        });
    }

    [Fact]
    public async Task DisposalStopsTheTimerAndFencesAnUncooperativeAutomaticResponse()
    {
        var clock = new ManualClock(new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var history = new SalesService();
        await using var services = Services(history, clock);
        var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.MountAsync();
            history.HoldNext = true;
            clock.Advance(TimeSpan.FromMinutes(5));
            await renderer.WaitForAsync(() => history.Calls.Count == 2);
            Assert.Equal(2, history.Calls.Count);
            await renderer.DisposeAsync();
            Assert.Equal(0, clock.ActiveTimers);
            Assert.True(history.Calls[^1].Token.IsCancellationRequested);
            history.Complete(Today);
            clock.Advance(TimeSpan.FromMinutes(15));
            await Task.Yield();
            Assert.Equal(2, history.Calls.Count);
        });
    }

    [Fact]
    public async Task SuppliedDataDoesNotStartAnAutomaticRefreshOrCallTheService()
    {
        var clock = new ManualClock(new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var history = new SalesService();
        await using var services = Services(history, clock);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.MountAsync(Result());
            Assert.Equal(0, clock.ActiveTimers);
            clock.Advance(TimeSpan.FromMinutes(20));
            await Task.Yield();
            Assert.Empty(history.Calls);
        });
    }

    [Fact]
    public async Task TotalsShowExportEnergyValueAndEstimatedDepositWithTheirProvenance()
    {
        var html = await RenderAsync(Result());

        Assert.Matches("data-testid=\"sales-export\"[^>]*>5[.]00<small[^>]*>kWh", html);
        Assert.Matches("data-testid=\"sales-value\"[^>]*>1[.]20<small[^>]*>PLN", html);
        Assert.Matches("data-testid=\"sales-deposit\"[^>]*>1[.]48<small[^>]*>PLN", html);
        Assert.Contains("Deye estimate", html);
        Assert.Contains("After hourly netting", html);
        Assert.Contains("3.00 kWh", html);
        Assert.Contains("not a bank payout or the deposit balance", html);
        Assert.Contains("OSD billing meter", html);
        Assert.Contains("28 September 2026", html);
        Assert.Contains("1.23 multiplier", html);
        Assert.DoesNotContain("Paid", html);
        Assert.DoesNotContain("Available to withdraw", html);
    }

    [Fact]
    public async Task MeasuredZeroHasVisibleBaselineBarAndZeroTotals()
    {
        var data = Result() with
        {
            ExportKwh = 0, CreditedExportKwh = 0, EnergyValuePln = 0, EstimatedDepositPln = 0,
            Buckets = [new(Start, Start.AddHours(1), 0, 0, 0, 0, 1, 1, 1)]
        };
        var html = await RenderAsync(data);

        Assert.Equal(3, Regex.Matches(html, "data-testid=\"sales-(?:export|value|deposit)\"[^>]*>0[.]00").Count);
        var bar = Assert.Single(Regex.Matches(html, "<rect[^>]*data-testid=\"sales-bar\"[^>]*>"));
        Assert.Contains("y=\"232\"", bar.Value);
        Assert.Contains("height=\"1.5\"", bar.Value);
        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("Infinity", html);
        Assert.DoesNotContain("No export readings", html);
    }

    [Fact]
    public async Task MissingAndFutureBucketsRemainGapsWhilePartialTotalsAreExplicit()
    {
        var data = Result() with
        {
            ExpectedHours = 3, ObservedHours = 1, ValuedHours = 0, EnergyValuePln = null, EstimatedDepositPln = null,
            PriceError = "The price has not been published yet.",
            Buckets = [
                new(Start, Start.AddHours(1), 5, 3, null, null, 1, 1, 0),
                new(Start.AddHours(1), Start.AddHours(2), null, null, null, null, 1, 0, 0),
                new(Start.AddHours(2), Start.AddHours(3), null, null, null, null, 1, 0, 0),
                new(Start.AddDays(1), Start.AddDays(1).AddHours(1), 0, 0, 0, 0, 0, 0, 0)]
        };
        var html = await RenderAsync(data);

        Assert.Single(Regex.Matches(html, "data-testid=\"sales-bar\""));
        Assert.Matches("data-testid=\"sales-value\"[^>]*>—", html);
        Assert.Matches("data-testid=\"sales-deposit\"[^>]*>—", html);
        Assert.Contains("Partial data · totals for available hours", html);
        Assert.Contains("Readings 1 of 3 h · value 0 of 3 h", html);
        Assert.Contains("The price has not been published yet.", html);
        Assert.Contains("exported — kWh", html);
        Assert.DoesNotContain("value 0.00 PLN", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyPeriodsExplainContractBoundaryOrIncompleteCurrentHour(bool beforeContract)
    {
        var date = beforeContract ? new DateOnly(2026, 9, 27) : Today;
        var data = Result(new(ExportSalesPeriod.Day, date)) with
        {
            ExpectedHours = 0, ObservedHours = 0, ValuedHours = 0, Buckets = [],
            ExportKwh = null, CreditedExportKwh = null, EnergyValuePln = null, EstimatedDepositPln = null
        };
        var html = await RenderAsync(data);

        Assert.Contains(beforeContract ? "This period is before the contract start date — 28 September 2026." : "There are no completed hours in this period yet.", html);
        Assert.DoesNotContain("data-testid=\"sales-bar\"", html);
        Assert.Equal(3, Regex.Matches(html, "data-testid=\"sales-(?:export|value|deposit)\"[^>]*>—").Count);
    }

    [Fact]
    public async Task RepeatedAutumnHoursKeepBothOffsetsAndIndependentBars()
    {
        var repeated = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        var data = Result(new(ExportSalesPeriod.Day, new(2026, 10, 25))) with
        {
            Today = new(2026, 10, 25), ExpectedHours = 2, ObservedHours = 2, ValuedHours = 2,
            Buckets = [
                new(repeated, repeated.AddHours(1), 2, 1, 0.4m, 0.492m, 1, 1, 1),
                new(repeated.AddHours(1), repeated.AddHours(2), 3, 2, 0.8m, 0.984m, 1, 1, 1)]
        };
        var html = await RenderAsync(data);

        Assert.Contains("25 Oct 02:00 (+02:00)", html);
        Assert.Contains("25 Oct 02:00 (+01:00)", html);
        Assert.Equal(2, Regex.Matches(html, "data-testid=\"sales-bar\"").Count);
        Assert.Contains("2026-10-25T00:00:00.0000000+00:00", html);
        Assert.Contains("2026-10-25T01:00:00.0000000+00:00", html);
    }

    [Theory]
    [InlineData(ExportSalesPeriod.Month, 2026, 8, "August 2026")]
    [InlineData(ExportSalesPeriod.Year, 2025, 1, "2025")]
    public async Task CalendarPeriodsBeforeContractShowTheirSelectedWindowWithoutInventedSales(ExportSalesPeriod period, int year, int month, string caption)
    {
        var data = Result(new(period, new(year, month, 1))) with
        {
            ExpectedHours = 0, ObservedHours = 0, ValuedHours = 0, Buckets = [],
            ExportKwh = null, CreditedExportKwh = null, EnergyValuePln = null, EstimatedDepositPln = null
        };
        var html = await RenderAsync(data);

        Assert.Contains(caption, html);
        Assert.Contains("This period is before the contract start date — 28 September 2026.", html);
        Assert.DoesNotContain("data-testid=\"sales-bar\"", html);
    }

    [Fact]
    public async Task FailedDataSourceCannotMasqueradeAsAnEmptyCurrentPeriod()
    {
        var data = Result() with
        {
            ExpectedHours = 0, ObservedHours = 0, ValuedHours = 0, Buckets = [],
            ExportKwh = null, CreditedExportKwh = null, EnergyValuePln = null, EstimatedDepositPln = null,
            DataError = "Deye history is unavailable."
        };
        var html = await RenderAsync(data);

        Assert.Contains("Deye history is unavailable.", html);
        Assert.Contains("Data for this period is unavailable.", html);
        Assert.DoesNotContain("There are no completed hours", html);
        Assert.DoesNotContain("data-testid=\"sales-bar\"", html);
    }

    [Fact]
    public async Task CoordinatesAndEnglishAmountsUseDecimalPointsRegardlessOfServerCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            var html = await RenderAsync(Result());
            var bar = Assert.Single(Regex.Matches(html, "<rect[^>]*data-testid=\"sales-bar\"[^>]*>")).Value;
            Assert.DoesNotMatch("(?:x|y|width|height)=\"[^\"]*,", bar);
            Assert.Contains("1.20", html);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task CalendarNavigationRequestsDaysMonthsAndYearsAndTodayResetsTheSelection()
    {
        var history = new SalesService();
        await using var services = Services(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            Assert.Equal(new(ExportSalesPeriod.Day, Today), Assert.Single(history.Calls).Request);
            Assert.True(renderer.Control(root, "Next period").Disabled);
            await renderer.ClickAsync(root, "Previous period");
            Assert.Equal(new(ExportSalesPeriod.Day, Today.AddDays(-1)), history.Calls[^1].Request);
            await renderer.ClickAsync(root, "Month");
            await renderer.ClickAsync(root, "Previous period");
            Assert.Equal(new(ExportSalesPeriod.Month, new(2026, 8, 1)), history.Calls[^1].Request);
            await renderer.ClickAsync(root, "Year");
            await renderer.ClickAsync(root, "Previous period");
            Assert.Equal(new(ExportSalesPeriod.Year, new(2025, 1, 1)), history.Calls[^1].Request);
            await renderer.ClickAsync(root, "Today");
            Assert.Equal(new(ExportSalesPeriod.Day, Today), history.Calls[^1].Request);
            Assert.True(renderer.Control(root, "Next period").Disabled);
        });
    }

    [Fact]
    public async Task DateSelectorsPreserveCalendarBoundariesAndCustomLimitsAreInclusive()
    {
        var history = new SalesService();
        await using var services = Services(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            await renderer.ClickAsync(root, "Month");
            await renderer.ChangeAsync(root, "Month", "2024-02");
            Assert.Equal(new(ExportSalesPeriod.Month, new(2024, 2, 1)), history.Calls[^1].Request);
            await renderer.ClickAsync(root, "Year");
            await renderer.ChangeAsync(root, "Year", "2024");
            Assert.Equal(new(ExportSalesPeriod.Year, new(2024, 1, 1)), history.Calls[^1].Request);
            await renderer.ClickAsync(root, "Custom");
            await renderer.ChangeAsync(root, "Start date", "2025-09-30");
            await renderer.ChangeAsync(root, "End date, inclusive", "2026-09-30");
            await renderer.ClickAsync(root, "Apply");
            Assert.Equal(new(ExportSalesPeriod.Custom, Today, new(2025, 9, 30), Today), history.Calls[^1].Request);
            var count = history.Calls.Count;
            await renderer.ChangeAsync(root, "Start date", "2025-09-29");
            Assert.True(renderer.Control(root, "Apply").Disabled);
            Assert.Contains("no more than 366 days, inclusive", renderer.Text(root));
            await renderer.ChangeAsync(root, "Start date", "");
            await renderer.ChangeAsync(root, "End date, inclusive", "2026-09-29");
            Assert.True(renderer.Control(root, "Apply").Disabled);
            Assert.Contains("Enter both dates", renderer.Text(root));
            Assert.DoesNotMatch("[\\u0400-\\u04ff]", renderer.Text(root));
            Assert.Equal(count, history.Calls.Count);
        });
    }

    [Fact]
    public async Task MetricSwitchChangesUnitsWithoutFetchingOrLosingEnergyWhenPricesAreMissing()
    {
        var history = new SalesService { MissingPrices = true };
        await using var services = Services(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            await renderer.ClickAsync(root, "Value");
            Assert.Contains("Energy value is not available yet", renderer.Text(root));
            Assert.DoesNotMatch("[\\u0400-\\u04ff]", renderer.Text(root));
            await renderer.ClickAsync(root, "Energy");
            Assert.DoesNotContain("Energy value is not available yet", renderer.Text(root));
            Assert.Contains("kWh", renderer.Text(root));
            Assert.Single(history.Calls);
        });
    }

    [Fact]
    public async Task FastNavigationCancelsTheOlderRequestAndRejectsItsLateResponse()
    {
        var history = new SalesService { HoldHistorical = true };
        await using var services = Services(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            var older = renderer.ClickAsync(root, "Previous period");
            var newer = renderer.ClickAsync(root, "Previous period");
            Assert.True(history.Calls[1].Token.IsCancellationRequested);
            Assert.Equal(Today.AddDays(-2), history.Calls[2].Request.Date);
            history.Complete(Today.AddDays(-2));
            await newer;
            history.Complete(Today.AddDays(-1));
            await older;
            Assert.Contains("28 September 2026", renderer.Text(root));
            Assert.DoesNotContain("29 September 2026", renderer.Text(root));
        });
    }

    [Fact]
    public async Task SignedNegativeAmountsRemainVisibleWhenTheContractAllowsThem()
    {
        var history = new SalesService { SignedNegativePrices = true };
        await using var services = Services(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            await renderer.ClickAsync(root, "Value");
            Assert.Contains("-0.60", renderer.Text(root));
            Assert.Contains("-0.74", renderer.Text(root));
            Assert.Contains("-0.5", renderer.Text(root));
            Assert.DoesNotMatch("[\\u0400-\\u04ff]", renderer.Text(root));
            Assert.Single(history.Calls);
        });
    }

    [Fact]
    public async Task AFailedRefreshPreservesLabeledOldTotalsAndRetryRecoversTheSameSelection()
    {
        var history = new SalesService();
        await using var services = Services(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            history.FailNext = true;
            await renderer.ClickAsync(root, "Refresh sales");
            Assert.Contains("Refresh failed. Showing previous data", renderer.Text(root));
            Assert.DoesNotMatch("[\\u0400-\\u04ff]", renderer.Text(root));
            Assert.Contains("5.00", renderer.Text(root));
            await renderer.ClickAsync(root, "Retry");
            Assert.DoesNotContain("Refresh failed", renderer.Text(root));
            Assert.Contains("5.00", renderer.Text(root));
            Assert.Equal(history.Calls[1].Request, history.Calls[2].Request);
        });
    }

    [Fact]
    public async Task LoadingStateUsesEnglishAndKeepsItsAccessibleControls()
    {
        var history = new SalesService { HoldHistorical = true };
        await using var services = Services(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            var pending = renderer.ClickAsync(root, "Previous period");
            Assert.Contains("Loading…", renderer.Text(root));
            Assert.Contains("Refresh sales", renderer.Text(root));
            Assert.DoesNotMatch("[\\u0400-\\u04ff]", renderer.Text(root));
            history.Complete(Today.AddDays(-1));
            await pending;
        });
    }

    [Theory]
    [InlineData("", "2026-09-30", "Enter both dates.")]
    [InlineData("1999-12-31", "2026-09-30", "The start date must be in 2000 or later.")]
    [InlineData("2026-09-30", "2026-09-29", "The end date must not be before the start date.")]
    [InlineData("2026-09-30", "2026-10-01", "The end date cannot be in the future.")]
    [InlineData("2025-09-29", "2026-09-30", "Choose no more than 366 days, inclusive.")]
    public async Task CustomDateValidationUsesEnglishAndDoesNotSubmitInvalidDates(string from, string through, string message)
    {
        var history = new SalesService();
        await using var services = Services(history);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            await renderer.ChangeAsync(root, "Date", "1999-12-31");
            Assert.Contains("Choose a date from 2000 onwards and no later than today.", renderer.Text(root));
            Assert.DoesNotMatch("[\\u0400-\\u04ff]", renderer.Text(root));
            Assert.Single(history.Calls);
            await renderer.ClickAsync(root, "Custom");
            var count = history.Calls.Count;
            await renderer.ChangeAsync(root, "Start date", from);
            await renderer.ChangeAsync(root, "End date, inclusive", through);
            Assert.Contains(message, renderer.Text(root));
            Assert.DoesNotMatch("[\\u0400-\\u04ff]", renderer.Text(root));
            Assert.True(renderer.Control(root, "Apply").Disabled);
            Assert.Equal(count, history.Calls.Count);
        });
    }

    [Fact]
    public async Task DisposalCancelsPendingLoadingAndIgnoresItsLateCompletion()
    {
        var history = new SalesService { HoldHistorical = true };
        await using var services = Services(history);
        var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            var pending = renderer.ClickAsync(root, "Previous period");
            await renderer.DisposeAsync();
            Assert.True(history.Calls[^1].Token.IsCancellationRequested);
            history.Complete(Today.AddDays(-1));
            await pending;
        });
    }

    private static ExportSalesResult Result(ExportSalesRequest? request = null) => new(
        request ?? new(ExportSalesPeriod.Day, Today), Today, new(2026, 9, 28), "Europe/Warsaw",
        Start, Start.AddHours(1), [new(Start, Start.AddHours(1), 5, 3, 1.2m, 1.476m, 1, 1, 1)],
        5, 3, 1.2m, 1.476m, 1, 1, 1);

    private static ExportSalesResult MultipleHours(ExportSalesRequest request)
    {
        var start = Start.AddDays(request.Date.DayNumber - Today.DayNumber);
        return Result(request) with
        {
            Start = start, End = start.AddDays(1), ExpectedHours = 3, ObservedHours = 3, ValuedHours = 3,
            ExportKwh = 6, CreditedExportKwh = 6, EnergyValuePln = 3, EstimatedDepositPln = 3.69m,
            Buckets = Enumerable.Range(0, 3).Select(index => new ExportSaleBucket(start.AddHours(index), start.AddHours(index + 1), index + 1, index + 1, 1, 1.23m, 1, 1, 1)).ToArray()
        };
    }

    private static ServiceProvider Services(IExportSalesService service, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NullJsRuntime>();
        services.AddSingleton<TimeProvider>(clock ?? new FixedClock());
        services.AddSingleton(service);
        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderAsync(ExportSalesResult result, bool overview = false, bool detailed = false)
    {
        var service = new SalesService();
        await using var services = Services(service);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SalesStatistics>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Data"] = result, ["Overview"] = overview, ["Detailed"] = detailed }));
            return output.ToHtmlString();
        });
        Assert.Empty(service.Calls);
        return WebUtility.HtmlDecode(html);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = now;
        public int ActiveTimers => _timers.Count(timer => !timer.Disposed);
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            var target = _now + duration;
            while (_timers.Where(timer => !timer.Disposed && timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault() is { } next)
            {
                _now = next.Due;
                next.Fire();
            }
            _now = target;
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public DateTimeOffset Due { get; private set; }
            private TimeSpan _period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                _period = period;
                return true;
            }
            public void Fire()
            {
                Due = _period <= TimeSpan.Zero ? DateTimeOffset.MaxValue : Due + _period;
                callback(state);
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class SalesService : IExportSalesService
    {
        public List<(ExportSalesRequest Request, CancellationToken Token)> Calls { get; } = [];
        public bool HoldHistorical { get; init; }
        public bool MissingPrices { get; init; }
        public bool SignedNegativePrices { get; init; }
        public bool FailNext { get; set; }
        public bool HoldNext { get; set; }
        public Func<ExportSalesRequest, ExportSalesResult>? ResultFactory { get; init; }
        private readonly Dictionary<DateOnly, TaskCompletionSource<ExportSalesResult>> _pending = [];
        public Task<ExportSalesResult> ReadAsync(ExportSalesRequest request, CancellationToken ct)
        {
            Calls.Add((request, ct));
            if (FailNext) { FailNext = false; throw new InvalidOperationException("A transient data-source failure."); }
            if (HoldNext || HoldHistorical && request.Date < Today)
            {
                HoldNext = false;
                // Complete canceled requests deliberately to exercise late-response fencing.
                var pending = new TaskCompletionSource<ExportSalesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add(request.Date, pending);
                return pending.Task;
            }
            var result = ResultFactory?.Invoke(request) ?? Result(request);
            if (MissingPrices) result = result with
            {
                EnergyValuePln = null, EstimatedDepositPln = null, ValuedHours = 0,
                Buckets = result.Buckets.Select(bucket => bucket with { EnergyValuePln = null, EstimatedDepositPln = null, ValuedHours = 0 }).ToArray()
            };
            if (SignedNegativePrices) result = result with
            {
                EnergyValuePln = -0.6m, EstimatedDepositPln = -0.738m,
                Buckets = result.Buckets.Select(bucket => bucket with { EnergyValuePln = -0.6m, EstimatedDepositPln = -0.738m }).ToArray()
            };
            return Task.FromResult(result);
        }
        public void Complete(DateOnly date)
        {
            var request = Calls.Last(call => call.Request.Date == date).Request;
            _pending[date].SetResult(ResultFactory?.Invoke(request) ?? Result(request));
        }
    }

    // Dispatch actual Blazor events so navigation and asynchronous fencing are tested through the UI.
    private sealed class EventRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
    {
        private TaskCompletionSource _displayChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            var previous = _displayChanged;
            _displayChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
            return Task.CompletedTask;
        }
        public async Task WaitForAsync(Func<bool> condition)
        {
            while (!condition()) await _displayChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        public async Task<int> MountAsync(ExportSalesResult? data = null, bool overview = false)
        {
            var root = AssignRootComponentId(InstantiateComponent(typeof(SalesStatistics)));
            await RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?> { ["Data"] = data, ["Overview"] = overview }));
            return root;
        }
        public IEnumerable<string?> Attributes(int componentId, string name)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            foreach (var frame in frames.Array.Take(frames.Count))
            {
                if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == name) yield return frame.AttributeValue?.ToString();
                if (frame.FrameType == RenderTreeFrameType.Component)
                    foreach (var value in Attributes(frame.ComponentId, name)) yield return value;
            }
        }
        public Task ClickAsync(int root, string label)
        {
            var control = Control(root, label);
            Assert.False(control.Disabled);
            Assert.NotEqual(0UL, control.EventId);
            return DispatchEventAsync(control.EventId, null, new MouseEventArgs());
        }
        public Task ChangeAsync(int root, string label, string value)
        {
            var control = Assert.Single(Controls(root), item => item.Label == label && item.Element == "input");
            Assert.NotEqual(0UL, control.EventId);
            return DispatchEventAsync(control.EventId, null, new ChangeEventArgs { Value = value });
        }
        public record ControlInfo(string Element, string Label, bool Disabled, ulong EventId);
        public ControlInfo Control(int root, string label) => Assert.Single(Controls(root), control => control.Label == label && control.Element == "button");
        private IEnumerable<ControlInfo> Controls(int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Component)
                    foreach (var control in Controls(frame.ComponentId)) yield return control;
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName is not ("button" or "input")) continue;
                string? label = null;
                var disabled = false;
                var eventId = 0UL;
                for (var j = i + 1; j < i + frame.ElementSubtreeLength; j++)
                {
                    var child = frames.Array[j];
                    if (child.FrameType != RenderTreeFrameType.Attribute) continue;
                    if (child.AttributeName == "aria-label") label = child.AttributeValue?.ToString();
                    if (child.AttributeName == "disabled") disabled = child.AttributeValue is true;
                    if (child.AttributeName is "onclick" or "onchange") eventId = child.AttributeEventHandlerId;
                }
                label ??= string.Concat(frames.Array.Skip(i + 1).Take(frame.ElementSubtreeLength - 1).Select(child => child.FrameType switch
                {
                    RenderTreeFrameType.Text => child.TextContent,
                    RenderTreeFrameType.Markup => WebUtility.HtmlDecode(Regex.Replace(child.MarkupContent, "<[^>]*>", "")),
                    _ => ""
                }));
                yield return new(frame.ElementName, label, disabled, eventId);
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
                RenderTreeFrameType.Attribute when frame.AttributeName is "aria-label" or "title" => frame.AttributeValue?.ToString(),
                _ => ""
            }));
        }
    }

    private sealed class NullJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}

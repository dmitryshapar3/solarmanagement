using System.Net;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Pages;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeyeSolar.Web.Tests;

public class EnergyDetailsTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    [Theory]
    [InlineData(-2742, "Battery charging", "2,742 W")]
    [InlineData(2742, "Battery discharging", "2,742 W")]
    [InlineData(0, "Battery idle", "0 W")]
    [InlineData(int.MinValue, "Battery charging", "2,147,483,648 W")]
    public async Task InverterFlowUsesBatteryPowerSignAndAbsoluteWattsWithoutChangingSolar(int power, string label, string value)
    {
        var timestamp = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var html = await RenderReadingsAsync(new() { SolarProduction = 4100, SolarObservedAt = timestamp,
            Timestamp = timestamp.AddMinutes(1), BatteryPower = power, BatteryVoltage = 51.5,
            BatteryCurrent = -4.2, BatteryTemperature = 24, BatterySoc = 87 }, timestamp.AddMinutes(2));
        Assert.Contains(label, html);
        Assert.Contains(value, html);
        Assert.Contains("4.10 kW", html);
        Assert.Contains("51.50 V", html);
        Assert.Contains("-4.20 A", html);
        Assert.Contains("24.00 °C", html);
        Assert.Contains("12:00:00", html);
        Assert.Contains("12:01:00", html);
    }

    [Fact]
    public async Task MissingSolarMeasurementRemainsUnavailableEvenWhenTheSourceDefaultsPowerToZero()
    {
        var html = await RenderReadingsAsync(new() { SolarProduction = 0, SolarObservedAt = null }, DateTimeOffset.UtcNow);
        Assert.Matches("data-testid=\"inverter-pv\"[^>]*>— kW", html);
        Assert.Contains("Deye solar measurement", html);
        Assert.Contains("Unavailable", html);
    }

    [Fact]
    public void NewDetailRoutesInheritAuthorizationAndNeverAllowAnonymousAccess()
    {
        foreach (var (page, path) in new[] { (typeof(InverterDetails), "/inverter-details"), (typeof(SolarDetails), "/solar-details"), (typeof(SalesDetails), "/sales-details") })
        {
            Assert.Equal(path, Assert.Single(page.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>()).Template);
            Assert.NotEmpty(page.GetCustomAttributes(true).OfType<IAuthorizeData>());
            Assert.Empty(page.GetCustomAttributes(true).OfType<IAllowAnonymous>());
        }
    }

    [Theory]
    [InlineData("https://foreign.example")]
    [InlineData("//foreign.example")]
    [InlineData("/settings")]
    [InlineData("/sales?returnTo=https://foreign.example")]
    public void DetailReturnLinksStayOnAnApprovedReadOnlySurface(string value) => Assert.Equal("/", EnergyDetailsNavigation.ReturnPath(value));

    [Theory]
    [InlineData(ExportSalesPeriod.Day)]
    [InlineData(ExportSalesPeriod.Month)]
    [InlineData(ExportSalesPeriod.Year)]
    [InlineData(ExportSalesPeriod.Custom)]
    public void SalesDetailsNavigationRoundTripsTheSelectedWindow(ExportSalesPeriod period)
    {
        var request = new ExportSalesRequest(period, new(2026, 9, 30),
            period == ExportSalesPeriod.Custom ? new(2026, 9, 1) : null,
            period == ExportSalesPeriod.Custom ? new(2026, 9, 30) : null);
        var url = EnergyDetailsNavigation.SalesUrl("/sales-details", request, "/sales");
        var query = QueryHelpers.ParseQuery(new Uri("https://local.invalid" + url).Query);
        Assert.True(EnergyDetailsNavigation.TrySalesRequest(query["period"].ToString(), query["date"].ToString(), query.ContainsKey("from") ? query["from"].ToString() : null,
            query.ContainsKey("through") ? query["through"].ToString() : null, Today, out var parsed));
        Assert.Equal(request, parsed);
        Assert.Equal("/sales", query["returnTo"].ToString());
    }

    [Theory]
    [InlineData("999", "2026-09-30", null, null)]
    [InlineData("Day", "2026-02-30", null, null)]
    [InlineData("Day", "2026-10-02", null, null)]
    [InlineData("Custom", "2026-09-30", "2024-01-01", "2026-09-30")]
    [InlineData("Custom", "2026-09-30", "2026-10-01", "2026-09-30")]
    public void InvalidAndUnboundedSalesWindowsAreRejectedBeforeFetching(string period, string date, string? from, string? through)
        => Assert.False(EnergyDetailsNavigation.TrySalesRequest(period, date, from, through, Today, out _));

    private static async Task<string> RenderReadingsAsync(InverterData data, DateTimeOffset now)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<InverterReadings>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Data"] = data, ["Now"] = now, ["TimeZoneId"] = "Europe/Warsaw" }))).ToHtmlString()));
    }
}

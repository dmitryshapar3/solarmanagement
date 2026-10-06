using System.Net;
using System.Text.RegularExpressions;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Components.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Tests;

public class ReadingsSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static InverterData Reading() => ConfirmedInverterReading.Create(new()
    {
        Timestamp = Now, SolarObservedAt = Now.AddMinutes(-1), GridObservedAt = Now.AddMinutes(-1),
        SolarDeviceSn = "primary", GridDeviceSn = "primary", SolarProduction = 0,
        GridConsumption = -1200, BatteryPower = -2400, LoadPower = 900, BatterySoc = 75,
        BatteryVoltage = 51.5, BatteryCurrent = -4.2, BatteryTemperature = 24
    });

    [Fact]
    public async Task SnapshotKeepsConfirmedZeroAndSignedValuesAndExplainsTheBalance()
    {
        var html = await Render(Reading(), "Trusted inverter provider");
        Assert.Contains("0.00", Value(html, "solar"));
        Assert.Contains("−1.20", Value(html, "grid"));
        Assert.Contains("−2.40", Value(html, "battery"));
        Assert.Contains("75", Value(html, "charge"));
        Assert.Contains("−4500", Value(html, "balance"));
        Assert.Contains("Solar measured 6 Oct 11:59:00", html);
        Assert.Contains("Polled 6 Oct 12:00:00", html);
        Assert.Contains("Source: Trusted inverter provider", html);
        Assert.Contains("export and charging are negative", html);
        Assert.Contains("not a measurement of inverter losses", html);
    }

    [Fact]
    public async Task OldPollAndMeasurementsBecomeUnavailableWithoutInventingZeros()
    {
        var html = await Render(Reading() with { Timestamp = Now.AddMinutes(-11), SolarObservedAt = Now.AddMinutes(-12), GridObservedAt = Now.AddMinutes(-12) });
        foreach (var metric in new[] { "solar", "grid", "battery", "load", "charge", "voltage", "current", "temperature", "balance" })
            Assert.Contains("—", Value(html, metric));
        Assert.Contains("Required power readings are unavailable", html);
    }

    [Fact]
    public async Task MissingQualityDoesNotHideAnIndependentValidGridMeasurement()
    {
        var data = Reading();
        data = data with { Telemetry = ((InverterTelemetry)data.Telemetry!) with
            { SolarPower = new(new Watts(0), data.SolarObservedAt, MeasurementQuality.Invalid) } };
        var html = await Render(data);
        Assert.Contains("—", Value(html, "solar"));
        Assert.Contains("−1.20", Value(html, "grid"));
        Assert.Contains("—", Value(html, "balance"));
        Assert.DoesNotContain("0.00", Value(html, "solar"));
    }

    [Fact]
    public async Task EmptySnapshotUsesUnavailableTilesAndNoGuessedProvider()
    {
        var html = await Render(null);
        Assert.Contains("Source: Inverter", html);
        Assert.Contains("Polled Unavailable", html);
        Assert.Contains("Solar measured Unavailable", html);
        Assert.Contains("—", Value(html, "balance"));
        Assert.DoesNotContain("Deye", html);
    }

    private static string Value(string html, string name) => Regex.Match(html,
        "data-testid=\"snapshot-" + name + "\"[^>]*>([\\s\\S]*?)</(?:dd|strong)>").Groups[1].Value;

    private static async Task<string> Render(InverterData? data, string? providerName = null)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddComponentLocalization();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<ReadingsSnapshot>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Data"] = data, ["ExpectedDevice"] = "primary",
                ["Now"] = Now, ["TimeZoneId"] = "UTC", ["ProviderName"] = providerName }))).ToHtmlString()));
    }
}

using System.Net;
using System.Xml.Linq;
using DeyeSolar.Web.Components.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeyeSolar.Web.Tests;

public class EnergyFlowPresentationTests
{
    [Theory]
    [InlineData("solar", 2, "toward-junction")]
    [InlineData("battery", 2, "toward-junction")]
    [InlineData("battery", -2, "away-from-junction")]
    [InlineData("grid", 2, "toward-junction")]
    [InlineData("grid", -2, "away-from-junction")]
    [InlineData("load", 2, "away-from-junction")]
    public void ConnectionsFollowTheReportedPowerDirection(string key, double kilowatts, string direction)
    {
        var connection = Assert.Single(EnergyFlowPresentation.Connections(Reading(kilowatts)), item => item.Key == key);
        Assert.True(connection.Active);
        Assert.Equal(direction, connection.Direction);
    }

    [Fact]
    public async Task NegativeSolarAndLoadAreUnavailableWhileSignedGridAndBatteryRemainVisible()
    {
        var reading = Reading(-2);
        var connections = EnergyFlowPresentation.Connections(reading);
        Assert.All(connections.Where(item => item.Key is "solar" or "load"), item =>
        {
            Assert.False(item.Active);
            Assert.Equal("unknown", item.State);
        });
        Assert.All(connections.Where(item => item.Key is "grid" or "battery"), item => Assert.True(item.Active));
        var document = Document(await RenderAsync(reading));
        foreach (var key in new[] { "solar", "load" })
        {
            var node = document.Descendants("g").Single(group => (string?)group.Attribute("data-flow-node") == key);
            Assert.Contains(node.Descendants("text"), text => text.Value == "— kW");
        }
        Assert.Equal(2, document.Descendants("g").Count(group => (string?)group.Attribute("class") == "flow-arrows"));
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData(0d, "idle")]
    [InlineData(double.NaN, "unknown")]
    [InlineData(double.PositiveInfinity, "unknown")]
    public async Task UnavailableAndZeroMeasurementsHaveNoDirectionalArrows(double? kilowatts, string state)
    {
        var connections = EnergyFlowPresentation.Connections(Reading(kilowatts));
        Assert.All(connections, connection =>
        {
            Assert.False(connection.Active);
            Assert.False(connection.Reverse);
            Assert.Equal("none", connection.Direction);
            Assert.Equal(state, connection.State);
        });
        var html = await RenderAsync(Reading(kilowatts));
        Assert.DoesNotContain("flow-arrows", html);
        Assert.DoesNotContain("flow-chevron", html);
        if (state == "unknown")
        {
            Assert.Contains("Grid state unknown", html);
            Assert.Contains("Battery state unknown", html);
            Assert.DoesNotContain("Grid idle", html);
            Assert.DoesNotContain("Battery idle", html);
        }
        Assert.Equal(4, Document(html).Descendants("path").Count(path => (string?)path.Attribute("class") == "flow-line " + state));
    }

    [Fact]
    public async Task LoadNodeContainsItsActualReadingAndEachConnectionEndsAtANamedNode()
    {
        var reading = new LiveReading(4.12, 1.78, -.5, -1.84, 67, null, null, null);
        var document = Document(await RenderAsync(reading));
        var nodes = document.Descendants("g").Where(group => group.Attribute("data-flow-node") is not null).ToArray();
        Assert.Equal(new[] { "solar", "battery", "grid", "load" }, nodes.Select(node => (string)node.Attribute("data-flow-node")!));
        var load = Assert.Single(nodes, node => (string?)node.Attribute("data-flow-node") == "load");
        Assert.Contains(load.Descendants("text"), text => text.Value == "Load");
        Assert.Contains(load.Descendants("text"), text => text.Value == "1.78 kW");
        foreach (var node in nodes)
        {
            Assert.Single(document.Descendants("g"), group => (string?)group.Attribute("data-flow") == (string?)node.Attribute("data-flow-node"));
            Assert.Contains(node.Descendants("text"), text => (string?)text.Attribute("class") == "flow-power");
        }
        Assert.Equal("away-from-junction", (string?)document.Descendants("g").Single(group => (string?)group.Attribute("data-flow") == "load").Attribute("data-direction"));
    }

    [Fact]
    public async Task MissingLoadRemainsUnavailableInItsNodeWithoutHidingTheOtherReadings()
    {
        var html = await RenderAsync(new LiveReading(4.12, null, -.5, 1.84, 67, null, null, null));
        var document = Document(html);
        var load = document.Descendants("g").Single(group => (string?)group.Attribute("data-flow-node") == "load");
        Assert.Contains(load.Descendants("text"), text => text.Value == "— kW");
        var loadRoute = document.Descendants("g").Single(group => (string?)group.Attribute("data-flow") == "load");
        Assert.DoesNotContain(loadRoute.Descendants("g"), group => (string?)group.Attribute("class") == "flow-arrows");
        Assert.Equal(3, document.Descendants("g").Count(group => (string?)group.Attribute("class") == "flow-arrows"));
        Assert.Contains("4.12 kW", html);
        Assert.DoesNotContain("flow-rule-track", html);
        Assert.DoesNotContain("role=\"meter\"", html);
        Assert.DoesNotContain("Signs come from the inverter", html);
    }

    private static LiveReading Reading(double? kilowatts) => new(kilowatts, kilowatts, kilowatts, kilowatts, 67, null, null, null);
    private static XDocument Document(string html) => XDocument.Parse("<root>" + html + "</root>");
    private static async Task<string> RenderAsync(LiveReading data)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddComponentLocalization();
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<EnergyFlow>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Data"] = data }))).ToHtmlString()));
    }
}

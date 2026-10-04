using System.Net;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.ProviderE2E;

public class GrowattProviderE2ETests
{
    private const string Source = "https://www.showdoc.com.cn/262556420217021/6129822090975531";
    private static readonly string[] Origins = ["https://openapi.growatt.com"];
    private static readonly Dictionary<string, string> Secrets = new() { ["apiToken"] = "fixture-growatt-token" };
    private static object Values(string unit = "W") => new { baseUrl = "https://openapi.growatt.com", username = "fixture user", deviceType = "7", powerUnit = unit };
    private const long Observed = 1780000000000;

    [Fact]
    public async Task DocumentedInventoryAndTelemetryFlowPreservesUnitsSignsAndEpoch()
    {
        await using var server = CreateServer(Telemetry());
        await using var worker = await ProviderWorkerSession.StartAsync("GrowattCloud", "growatt.cloud", server, Values(), Secrets, Origins);
        Assert.True((await worker.CallAsync("test", new { })).GetProperty("success").GetBoolean());
        var discovery = Assert.Single((await worker.CallAsync("discover", new { })).EnumerateArray());
        Assert.Equal("MIN-FIXTURE", discovery.GetProperty("remoteId").GetString());
        var capabilities = discovery.GetProperty("metadata").GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("hasBattery").GetBoolean());
        Assert.False(capabilities.GetProperty("hasGridPowerHistory").GetBoolean());
        var result = (await worker.CallAsync("inverter.read", new { remoteId = "MIN-FIXTURE" })).Deserialize<ProviderInverterTelemetry>(IntegrationJson.Options)!;
        Assert.Equal(81m, result.BatterySoc.Value);
        Assert.Equal(3600m, result.SolarPower.Value);
        Assert.Equal(-600m, result.GridPower.Value);
        Assert.Equal(200m, result.BatteryPower.Value);
        Assert.Equal(3200m, result.LoadPower.Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Observed), result.GridPower.ObservedAt);
        Assert.Equal(ProviderMeasurementQuality.Missing, result.BatteryVoltage.Quality);
        // Test, discovery and read share the process cache: no undocumented repeated plant calls.
        Assert.Equal(3, server.Requests.Count);
        Assert.True((await worker.CallResponseAsync("inverter.history", new { remoteId = "MIN-FIXTURE",
            start = "2026-10-01T00:00:00Z", end = "2026-10-02T00:00:00Z" })).TryGetProperty("error", out _));
        Assert.Equal(3, server.Requests.Count);
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("kW", 3600000, -600000)]
    [InlineData("unknown", null, null)]
    public async Task ExplicitUnitVerificationControlsNormalization(string unit, int? solar, int? grid)
    {
        await using var server = CreateServer(Telemetry());
        await using var worker = await ProviderWorkerSession.StartAsync("GrowattCloud", "growatt.cloud", server, Values(unit), Secrets, Origins);
        var result = (await worker.CallAsync("inverter.read", new { remoteId = "MIN-FIXTURE" })).Deserialize<ProviderInverterTelemetry>(IntegrationJson.Options)!;
        Assert.Equal(solar is null ? null : (decimal?)solar, result.SolarPower.Value);
        Assert.Equal(grid is null ? null : (decimal?)grid, result.GridPower.Value);
        Assert.Equal(unit == "unknown" ? ProviderMeasurementQuality.Invalid : ProviderMeasurementQuality.Good, result.GridPower.Quality);
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task MissingTelemetryAndPlainMinDefaultBmsZerosAreNotInventedBatteryData()
    {
        var data = Telemetry();
        data.Remove("ppv"); data.Remove("pacToGridTotal"); data["batteryNo"] = 0;
        await using var server = CreateServer(data);
        await using var worker = await ProviderWorkerSession.StartAsync("GrowattCloud", "growatt.cloud", server, Values(), Secrets, Origins);
        var device = Assert.Single((await worker.CallAsync("discover", new { })).EnumerateArray());
        Assert.False(device.GetProperty("metadata").GetProperty("capabilities").GetProperty("hasBattery").GetBoolean());
        var result = (await worker.CallAsync("inverter.read", new { remoteId = "MIN-FIXTURE" })).Deserialize<ProviderInverterTelemetry>(IntegrationJson.Options)!;
        Assert.Equal(ProviderMeasurementQuality.Missing, result.SolarPower.Quality);
        Assert.Equal(ProviderMeasurementQuality.Missing, result.GridPower.Quality);
        Assert.Equal(ProviderMeasurementQuality.Missing, result.BatterySoc.Quality);
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(200, "{\"error_code\":10001,\"error_msg\":\"denied\"}")]
    public async Task HttpOrApiAuthenticationFailureCannotConnect(int code, string response)
    {
        await using var server = new MockVendorServer(Source, request =>
        {
            Assert.Equal("fixture-growatt-token", request.Header("token"));
            Assert.Equal("/v1/plant/user_plant_list", request.Path);
            return FixtureResponse.Raw(response, code);
        });
        await using var worker = await ProviderWorkerSession.StartAsync("GrowattCloud", "growatt.cloud", server, Values(), Secrets, Origins);
        Assert.True((await worker.CallResponseAsync("test", new { })).TryGetProperty("error", out _));
        Assert.Single(server.Requests);
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task ForeignTelemetrySerialAndUnregisteredReadsAreRejected()
    {
        var data = Telemetry(); data["serialNum"] = "FOREIGN";
        await using (var server = CreateServer(data))
        await using (var worker = await ProviderWorkerSession.StartAsync("GrowattCloud", "growatt.cloud", server, Values(), Secrets, Origins))
        {
            Assert.True((await worker.CallResponseAsync("discover", new { })).TryGetProperty("error", out _));
            server.AssertNoProtocolFailures();
        }
        await using var valid = CreateServer(Telemetry());
        await using var session = await ProviderWorkerSession.StartAsync("GrowattCloud", "growatt.cloud", valid, Values(), Secrets, Origins);
        Assert.True((await session.CallResponseAsync("inverter.read", new { remoteId = "NOT-AUTHORIZED" })).TryGetProperty("error", out _));
        Assert.Equal(3, valid.Requests.Count);
        valid.AssertNoProtocolFailures();
    }

    private static Dictionary<string, object> Telemetry() => new()
    {
        ["serialNum"] = "MIN-FIXTURE", ["calendar"] = new { time = new { time = Observed } },
        ["batteryNo"] = 1, ["bmsSoc"] = 81, ["ppv"] = 3600,
        ["pacToUserTotal"] = 400, ["pacToGridTotal"] = 1000, ["pacToLocalLoad"] = 3200,
        ["bdc1DischargePower"] = 500, ["bdc1ChargePower"] = 100,
        ["bdc2DischargePower"] = 0, ["bdc2ChargePower"] = 200
    };

    private static MockVendorServer CreateServer(Dictionary<string, object> data) => new(Source, request =>
    {
        Assert.Equal("https", request.Uri.Scheme);
        Assert.Equal("openapi.growatt.com", request.Uri.Host);
        Assert.Equal("fixture-growatt-token", request.Header("token"));
        if (request.Path == "/v1/plant/user_plant_list")
        {
            Assert.Equal("POST", request.Method);
            Assert.StartsWith("application/x-www-form-urlencoded", request.Header("Content-Type"));
            var form = Form(request.Body);
            Assert.Equal("fixture user", form["user_name"]); Assert.Equal("1", form["page"]); Assert.Equal("100", form["perpage"]);
            return FixtureResponse.Json(new { error_code = 0, error_msg = "", data = new { count = 1, plants = new[] { new { plant_id = 24765, name = "Fixture plant" } } } });
        }
        if (request.Path == "/v1/device/list")
        {
            Assert.Equal("GET", request.Method);
            var query = Form(request.Uri.Query.TrimStart('?'));
            Assert.Equal("24765", query["plant_id"]); Assert.Equal("1", query["page"]); Assert.Equal("100", query["perpage"]);
            return FixtureResponse.Json(new { error_code = 0, error_msg = "", data = new { count = 2, devices = new[]
                { new { device_sn = "MIN-FIXTURE", type = 7, model = "MIN 10KTL-XH" }, new { device_sn = "SPH-UNSUPPORTED", type = 5, model = "SPH" } } } });
        }
        Assert.Equal("/v1/device/tlx/tlx_last_data", request.Path);
        Assert.Equal("POST", request.Method);
        Assert.StartsWith("application/x-www-form-urlencoded", request.Header("Content-Type"));
        var body = Form(request.Body);
        Assert.Single(body); Assert.Equal("MIN-FIXTURE", body["tlx_sn"]);
        return FixtureResponse.Json(new { error_code = 0, error_msg = "", data });
    });

    private static Dictionary<string, string> Form(string body) => body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(item => item.Split('=', 2))
        .ToDictionary(item => WebUtility.UrlDecode(item[0]), item => WebUtility.UrlDecode(item[1]), StringComparer.Ordinal);
}

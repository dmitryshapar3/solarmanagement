using System.Text.Json;

namespace SolarManagement.ProviderE2E;

public class ShellyProviderE2ETests
{
    private const string Source = "https://shelly-api-docs.shelly.cloud/cloud-control-api/communication-v2/";
    private static readonly string[] Origins = ["https://*.shelly.cloud"];
    private static readonly Dictionary<string, string> Secrets = new() { ["authKey"] = "fixture-key" };
    private static readonly object Values = new { serverUri = "https://fixture.shelly.cloud", requestIntervalMilliseconds = 1000 };

    // Add-on ID and meter layout come from:
    // https://shelly-api-docs.shelly.cloud/gen2/Addons/ShellyProOutputAddon/
    // https://shelly-api-docs.shelly.cloud/gen2/ComponentsAndServices/EM/#status
    [Fact]
    public async Task Pro3EmAddonIsDiscoveredMeasuredAndSwitchedThroughFramedWorker()
    {
        var state = true;
        await using var server = new MockVendorServer(Source, request =>
        {
            ValidateCloudRequest(request);
            if (request.Path == "/device/all_status")
                return FixtureResponse.Json(new { isok = true, data = new { devices_status = new Dictionary<string, object> { ["pro3em"] = DeviceStatus(state) } } });
            if (request.Path == "/v2/devices/api/get")
            {
                Assert.Equal("pro3em", request.Json.GetProperty("ids")[0].GetString());
                Assert.Contains("status", request.Json.GetProperty("select").EnumerateArray().Select(item => item.GetString()));
                return FixtureResponse.Json(new[] { new { id = "pro3em", code = "SPEM-003CEBEU", online = 1, status = DeviceStatus(state) } });
            }
            Assert.Equal("/v2/devices/api/set/switch", request.Path);
            Assert.Equal("pro3em", request.Json.GetProperty("id").GetString());
            Assert.Equal(100, request.Json.GetProperty("channel").GetInt32());
            state = request.Json.GetProperty("on").GetBoolean();
            return FixtureResponse.Raw("{}");
        });
        await using var worker = await ProviderWorkerSession.StartAsync("ShellyCloud", "shelly.cloud", server, Values, Secrets, Origins);
        Assert.True((await worker.CallAsync("test", new { })).GetProperty("success").GetBoolean());
        var device = Assert.Single((await worker.CallAsync("discover", new { })).EnumerateArray());
        Assert.Equal("100", device.GetProperty("channel").GetString());
        Assert.True(device.GetProperty("metadata").GetProperty("capabilities").GetProperty("canMeasurePower").GetBoolean());
        var reading = await worker.CallAsync("socket.read", new { remoteId = "pro3em", channel = "100" });
        Assert.Equal(3600, reading.GetProperty("currentPowerWatts").GetInt32());
        foreach (var desired in new[] { false, true })
        {
            var commandId = Guid.NewGuid().ToString("D");
            var command = await worker.CallAsync("socket.set", new { remoteId = "pro3em", channel = "100", isOn = desired, commandId });
            Assert.Equal(commandId, command.GetProperty("commandId").GetString());
            Assert.Equal("Acknowledged", command.GetProperty("status").GetString());
            var observed = await worker.CallAsync("socket.read", new { remoteId = "pro3em", channel = "100" });
            Assert.Equal(desired, observed.GetProperty("isOn").GetBoolean());
        }
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-100)]
    public async Task MissingOrExportPowerIsUnavailableUnderConsumptionContract(int? watts)
    {
        await using var server = new MockVendorServer(Source, request =>
        {
            ValidateCloudRequest(request);
            Assert.Equal("/v2/devices/api/get", request.Path);
            return FixtureResponse.Json(new[] { new { id = "pro3em", code = "SPEM-003CEBEU", online = 1,
                status = new Dictionary<string, object> { ["switch:100"] = new { output = true }, ["em:0"] = new { total_act_power = watts } } } });
        });
        await using var worker = await ProviderWorkerSession.StartAsync("ShellyCloud", "shelly.cloud", server, Values, Secrets, Origins);
        var result = await worker.CallAsync("socket.read", new { remoteId = "pro3em", channel = "100" });
        Assert.Equal(JsonValueKind.Null, result.GetProperty("currentPowerWatts").ValueKind);
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(400, "{\"error\":\"DEVICE_INVALID_CHANNEL\"}")]
    [InlineData(202, "{}")]
    [InlineData(200, "{\"error\":\"DEVICE_FAILED_COMMAND\"}")]
    public async Task AuthenticationFailureOrMissingAcknowledgementNeverReportsSuccess(int status, string response)
    {
        await using var server = new MockVendorServer(Source, request =>
        {
            ValidateCloudRequest(request);
            Assert.Equal("/v2/devices/api/set/switch", request.Path);
            Assert.Equal(100, request.Json.GetProperty("channel").GetInt32());
            return FixtureResponse.Raw(response, status);
        });
        await using var worker = await ProviderWorkerSession.StartAsync("ShellyCloud", "shelly.cloud", server, Values, Secrets, Origins);
        var result = await worker.CallResponseAsync("socket.set", new { remoteId = "pro3em", channel = "100", isOn = true, commandId = Guid.NewGuid().ToString("D") });
        Assert.True(result.TryGetProperty("error", out _));
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task ForeignDeviceResponseAndOutOfRangeComponentAreRejected()
    {
        await using var server = new MockVendorServer(Source, request =>
        {
            ValidateCloudRequest(request);
            return FixtureResponse.Json(new[] { new { id = "someone-else", code = "SPEM-003CEBEU", online = 1, status = DeviceStatus(true) } });
        });
        await using var worker = await ProviderWorkerSession.StartAsync("ShellyCloud", "shelly.cloud", server, Values, Secrets, Origins);
        Assert.True((await worker.CallResponseAsync("socket.read", new { remoteId = "pro3em", channel = "100" })).TryGetProperty("error", out _));
        Assert.True((await worker.CallResponseAsync("socket.read", new { remoteId = "pro3em", channel = "200" })).TryGetProperty("error", out _));
        Assert.Single(server.Requests);
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task UnrelatedSwitchIsNotAssignedTheDevicesAggregateMeter()
    {
        await using var server = new MockVendorServer(Source, request =>
        {
            ValidateCloudRequest(request);
            return FixtureResponse.Json(new[] { new { id = "other-device", code = "SPSW-001PE16EU", online = 1,
                status = new Dictionary<string, object> { ["switch:100"] = new { output = true }, ["em:0"] = new { total_act_power = 3600 } } } });
        });
        await using var worker = await ProviderWorkerSession.StartAsync("ShellyCloud", "shelly.cloud", server, Values, Secrets, Origins);
        var result = await worker.CallAsync("socket.read", new { remoteId = "other-device", channel = "100" });
        Assert.Equal(JsonValueKind.Null, result.GetProperty("currentPowerWatts").ValueKind);
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task CancelledHttpOperationDoesNotBlockFollowingWorkerRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var server = new MockVendorServer(Source, async (request, ct) =>
        {
            ValidateCloudRequest(request);
            if (Interlocked.Increment(ref count) == 1)
            {
                started.SetResult();
                await release.Task.WaitAsync(ct);
            }
            return FixtureResponse.Json(new[] { new { id = "pro3em", code = "SPEM-003CEBEU", online = 1, status = DeviceStatus(true) } });
        });
        await using var worker = await ProviderWorkerSession.StartAsync("ShellyCloud", "shelly.cloud", server, Values, Secrets, Origins);
        using var cancelled = new CancellationTokenSource();
        var pending = worker.CallAsync("socket.read", new { remoteId = "pro3em", channel = "100" }, cancelled.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            var result = await worker.CallAsync("socket.read", new { remoteId = "pro3em", channel = "100" });
            Assert.Equal(3600, result.GetProperty("currentPowerWatts").GetInt32());
        }
        finally { release.TrySetResult(); }
        server.AssertNoProtocolFailures();
    }

    private static object DeviceStatus(bool on) => new Dictionary<string, object>
    {
        ["_dev_info"] = new { id = "pro3em", code = "SPEM-003CEBEU", online = true },
        ["switch:100"] = new { output = on },
        ["em:0"] = new { id = 0, a_act_power = 1200, b_act_power = 1300, c_act_power = 1100, total_act_power = 3600 }
    };

    private static void ValidateCloudRequest(FixtureRequest request)
    {
        Assert.Equal("POST", request.Method);
        Assert.Equal("https", request.Uri.Scheme);
        Assert.Equal("fixture.shelly.cloud", request.Uri.Host);
        Assert.Contains("auth_key=fixture-key", request.Uri.Query);
    }
}

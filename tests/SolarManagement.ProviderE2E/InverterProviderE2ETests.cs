using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.ProviderE2E;

public class InverterProviderE2ETests
{
    private const string SolisSource = "https://developer.soliscloud.com/guide/data-access-oauth2.html";
    private const string SungrowSource = "https://developer-api.isolarcloud.com/#/document/api?id=14058&project_id=1";
    private const string HuaweiSource = "https://support.huawei.com/enterprise/en/doc/EDOC1100492747/8f6c35d4/change-history";
    private static readonly DateTimeOffset Collected = DateTimeOffset.Parse("2024-07-25T10:00:00Z");
    private static readonly Dictionary<string, string> Token = new() { ["accessToken"] = "fixture-access-token" };
    private static object SolisValues => new { baseUrl = "https://api-oauth2.soliscloud.com", gridPositiveDirection = "import",
        batteryPositiveDirection = "discharge", historyPowerUnit = "W", plantUtcOffsetHours = 2 };
    private static object SungrowValues => new { baseUrl = "https://gateway.isolarcloud.eu", appKey = "fixture-app-key", deviceTimeZone = "Europe/Warsaw" };
    private static readonly Dictionary<string, string> SungrowSecrets = new() { ["accessToken"] = "fixture-access-token", ["accessKey"] = "fixture-access-key" };
    private static object HuaweiValues => new { baseUrl = "https://eu5.fusionsolar.huawei.com", authMode = "apiAccount", username = "fixture-api-user",
        gridPositiveDirection = "export", batteryPositiveDirection = "charge" };
    private static readonly Dictionary<string, string> HuaweiSecrets = new() { ["systemCode"] = "fixture-system-code" };

    [Fact]
    public async Task SolisDiscoversReadsAndLoadsPlantHistoryThroughActualWorkerFrames()
    {
        await using var server = new MockVendorServer(SolisSource, request =>
        {
            ValidateSolis(request);
            if (request.Path.EndsWith("inverterList"))
            {
                Assert.Equal(100, request.Json.GetProperty("pageSize").GetInt32());
                return SolisInventory();
            }
            if (request.Path.EndsWith("inverterDetail"))
            {
                Assert.Equal("solis-sn", request.Json.GetProperty("sn").GetString());
                return SolisData(new { sn = "solis-sn", stationId = "station-1", dataTimestamp = Collected.ToUnixTimeMilliseconds(),
                    uPv1 = 300, iPv1 = 10, uPv2 = 250, iPv2 = 8, batteryCapacitySoc = "73.2", batteryPower = 1.25,
                    batteryPowerStr = "kW", pSum = -0.5, pSumStr = "kW", familyLoadPower = 3500, familyLoadPowerStr = "W" });
            }
            Assert.EndsWith("stationDay", request.Path);
            Assert.Equal("station-1", request.Json.GetProperty("id").GetString());
            Assert.Equal("2024-07-25", request.Json.GetProperty("time").GetString());
            Assert.Equal(2, request.Json.GetProperty("timeZone").GetInt32());
            return SolisData(new[] { new { time = Collected.ToUnixTimeMilliseconds(), psum = -500 } });
        });
        await using var worker = await ProviderWorkerSession.StartAsync("SolisCloud", "solis.cloud", server, SolisValues, Token, ["https://api-oauth2.soliscloud.com"]);
        Assert.True((await worker.CallAsync("test", new { })).GetProperty("success").GetBoolean());
        var device = Assert.Single((await worker.CallAsync("discover", new { })).EnumerateArray());
        Assert.Equal("solis-sn", device.GetProperty("remoteId").GetString());
        var telemetry = await worker.CallAsync("inverter.read", new { remoteId = "solis-sn" });
        AssertMeasurement(telemetry, "solarPower", 5000, Collected);
        AssertMeasurement(telemetry, "batterySoc", 73.2m, Collected);
        AssertMeasurement(telemetry, "batteryPower", 1250, Collected);
        AssertMeasurement(telemetry, "gridPower", -500, Collected);
        AssertMeasurement(telemetry, "loadPower", 3500, Collected);
        Assert.Equal((int)ProviderMeasurementQuality.Missing, telemetry.GetProperty("batteryTemperature").GetProperty("quality").GetInt32());
        var history = await worker.CallAsync("inverter.history", History("solis-sn"));
        Assert.True(history.GetProperty("isComplete").GetBoolean());
        Assert.Equal(-500, Assert.Single(history.GetProperty("samples").EnumerateArray()).GetProperty("powerWatts").GetInt32());
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task SolisUnknownUnitsAndCollectionTimesNeverBecomeFreshWatts()
    {
        await using var server = new MockVendorServer(SolisSource, request =>
        {
            ValidateSolis(request);
            Assert.EndsWith("inverterDetail", request.Path);
            return SolisData(new { sn = "solis-sn", dataTimestamp = 0, uPv1 = 300, iPv1 = 10,
                batteryCapacitySoc = 101, batteryPower = 3, batteryPowerStr = "unknown", pSum = 2, psumStr = "kWh" });
        });
        await using var worker = await ProviderWorkerSession.StartAsync("SolisCloud", "solis.cloud", server, SolisValues, Token, ["https://api-oauth2.soliscloud.com"]);
        var telemetry = await worker.CallAsync("inverter.read", new { remoteId = "solis-sn" });
        AssertMeasurement(telemetry, "solarPower", 3000, null);
        foreach (var name in new[] { "batterySoc", "batteryPower", "gridPower" })
        {
            Assert.Equal((int)ProviderMeasurementQuality.Invalid, telemetry.GetProperty(name).GetProperty("quality").GetInt32());
            Assert.Equal(JsonValueKind.Null, telemetry.GetProperty(name).GetProperty("value").ValueKind);
            Assert.Equal(JsonValueKind.Null, telemetry.GetProperty(name).GetProperty("observedAt").ValueKind);
        }
        Assert.Equal((int)ProviderMeasurementQuality.Missing, telemetry.GetProperty("loadPower").GetProperty("quality").GetInt32());
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("unknown", null)]
    [InlineData("W", -2)]
    [InlineData("kW", -2000)]
    public async Task SolisModernOAuthPowerWithoutUnitRequiresVerifiedConfiguration(string unit, int? expectedWatts)
    {
        await using var server = new MockVendorServer(SolisSource, request =>
        {
            ValidateSolis(request);
            return SolisData(new { sn = "solis-sn", pSum = -2, batteryPower = 0, familyLoadPower = 10 });
        });
        await using var worker = await ProviderWorkerSession.StartAsync("SolisCloud", "solis.cloud", server,
            new { livePowerUnit = unit, gridPositiveDirection = "import", batteryPositiveDirection = "discharge" }, Token, ["https://api-oauth2.soliscloud.com"]);
        var reading = await worker.CallAsync("inverter.read", new { remoteId = "solis-sn" });
        if (expectedWatts.HasValue) AssertMeasurement(reading, "gridPower", expectedWatts.Value, null);
        else Assert.Equal((int)ProviderMeasurementQuality.Invalid, reading.GetProperty("gridPower").GetProperty("quality").GetInt32());
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData(200, "{\"success\":true,\"code\":0,\"data\":{\"sn\":\"someone-else\"}}")]
    [InlineData(200, "{malformed-json")]
    [InlineData(200, "{\"success\":false,\"code\":401,\"data\":null}")]
    [InlineData(401, "{}")]
    public async Task SolisForeignIdentityMalformedDataAndAuthorizationFailuresFailClosed(int status, string body)
    {
        await using var server = new MockVendorServer(SolisSource, request => { ValidateSolis(request); return FixtureResponse.Raw(body, status); });
        await using var worker = await ProviderWorkerSession.StartAsync("SolisCloud", "solis.cloud", server, SolisValues, Token, ["https://api-oauth2.soliscloud.com"]);
        var response = await worker.CallResponseAsync("inverter.read", new { remoteId = "solis-sn" });
        Assert.True(response.TryGetProperty("error", out _));
        Assert.DoesNotContain("fixture-access-token", response.GetRawText());
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task SolisPaginationUsesLastNumericIdPlusOneAndIncompletePointsRemainIncomplete()
    {
        await using var server = new MockVendorServer(SolisSource, request =>
        {
            ValidateSolis(request);
            if (request.Path.EndsWith("inverterList"))
            {
                if (!request.Json.TryGetProperty("minId", out var cursor))
                    return SolisData(new { page = new { records = Enumerable.Range(1, 100).Select(x => new { id = x, sn = "s" + x, stationId = "station-1", productModel = "2" }).ToArray() } });
                Assert.Equal(101, cursor.GetInt32());
                return SolisData(new { page = new { records = new[] { new { id = 101, sn = "s101", stationId = "station-1", productModel = "2" } } } });
            }
            Assert.EndsWith("stationDay", request.Path);
            return SolisData(new object[] { new { time = 0, psum = 25 }, new { time = Collected.ToUnixTimeMilliseconds(), psum = -100 } });
        });
        await using var worker = await ProviderWorkerSession.StartAsync("SolisCloud", "solis.cloud", server, SolisValues, Token, ["https://api-oauth2.soliscloud.com"]);
        Assert.Equal(101, (await worker.CallAsync("discover", new { })).GetArrayLength());
        var history = await worker.CallAsync("inverter.history", History("s101"));
        Assert.False(history.GetProperty("isComplete").GetBoolean());
        Assert.Single(history.GetProperty("samples").EnumerateArray());
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task SungrowUsesApprovedOAuthHeadersDocumentedPointIdsAndDirectionalWattPairs()
    {
        await using var server = new MockVendorServer(SungrowSource, request =>
        {
            ValidateSungrow(request);
            if (request.Path.EndsWith("queryPowerStationList")) return SungrowPlants();
            if (request.Path.EndsWith("getDeviceListByPsId")) return SungrowDevices(request);
            if (request.Path.EndsWith("getDeviceRealTimeData"))
            {
                Assert.Equal(14, request.Json.GetProperty("device_type").GetInt32());
                Assert.Equal("plant_14_1_1", request.Json.GetProperty("ps_key_list")[0].GetString());
                Assert.Contains("13003", request.Json.GetProperty("point_id_list").EnumerateArray().Select(x => x.GetString()));
                return SungrowData(new { device_point_list = new[] { new { device_point = SungrowPoint() } } });
            }
            Assert.EndsWith("getDevicePointMinuteDataList", request.Path);
            Assert.Equal("p13149,p13121", request.Json.GetProperty("points").GetString());
            Assert.Equal("20240725120000", request.Json.GetProperty("start_time_stamp").GetString());
            Assert.Equal("20240725130000", request.Json.GetProperty("end_time_stamp").GetString());
            return SungrowData(new Dictionary<string, object> { ["plant_14_1_1"] = new[] { new { time_stamp = "20240725120000", p13149 = "300", p13121 = "800" } } });
        });
        await using var worker = await ProviderWorkerSession.StartAsync("SungrowCloud", "sungrow.cloud", server, SungrowValues, SungrowSecrets, ["https://gateway.isolarcloud.eu"]);
        var discovery = await worker.CallAsync("discover", new { });
        Assert.Equal("plant_14_1_1", Assert.Single(discovery.EnumerateArray()).GetProperty("remoteId").GetString());
        var telemetry = await worker.CallAsync("inverter.read", new { remoteId = "plant_14_1_1" });
        AssertMeasurement(telemetry, "solarPower", 5000, Collected);
        AssertMeasurement(telemetry, "batterySoc", 72, Collected);
        AssertMeasurement(telemetry, "batteryPower", 200, Collected);
        AssertMeasurement(telemetry, "batteryTemperature", 27, Collected);
        AssertMeasurement(telemetry, "gridPower", -500, Collected);
        var history = await worker.CallAsync("inverter.history", History("plant_14_1_1"));
        Assert.True(history.GetProperty("isComplete").GetBoolean());
        Assert.Equal(-500, Assert.Single(history.GetProperty("samples").EnumerateArray()).GetProperty("powerWatts").GetInt32());
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task SungrowWithoutPlantTimeZoneKeepsValidValuesWithUnknownObservationTime()
    {
        await using var server = new MockVendorServer(SungrowSource, request =>
        {
            ValidateSungrow(request);
            if (request.Path.EndsWith("queryPowerStationList")) return SungrowPlants();
            if (request.Path.EndsWith("getDeviceListByPsId")) return SungrowDevices(request);
            Assert.EndsWith("getDeviceRealTimeData", request.Path);
            return SungrowData(new { device_point_list = new[] { new { device_point = SungrowPoint() } } });
        });
        await using var worker = await ProviderWorkerSession.StartAsync("SungrowCloud", "sungrow.cloud", server,
            new { appKey = "fixture-app-key" }, SungrowSecrets, ["https://gateway.isolarcloud.eu"]);
        var telemetry = await worker.CallAsync("inverter.read", new { remoteId = "plant_14_1_1" });
        AssertMeasurement(telemetry, "solarPower", 5000, null);
        Assert.True((await worker.CallResponseAsync("inverter.history", History("plant_14_1_1"))).TryGetProperty("error", out _));
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("foreign-device", "W")]
    [InlineData("plant_14_1_1", "kW")]
    public async Task SungrowForeignIdentityOrContradictingUnitCannotBeAccepted(string identity, string unit)
    {
        await using var server = new MockVendorServer(SungrowSource, request =>
        {
            ValidateSungrow(request);
            if (request.Path.EndsWith("queryPowerStationList")) return SungrowPlants();
            if (request.Path.EndsWith("getDeviceListByPsId")) return SungrowDevices(request);
            Assert.EndsWith("getDeviceRealTimeData", request.Path);
            return SungrowData(new { device_point_list = new[] { new { device_point = new { ps_key = identity, device_sn = "sg-sn", p13003 = "5" } } },
                point_dict = new[] { new { point_id = 13003, point_unit = unit } } });
        });
        await using var worker = await ProviderWorkerSession.StartAsync("SungrowCloud", "sungrow.cloud", server, SungrowValues, SungrowSecrets, ["https://gateway.isolarcloud.eu"]);
        Assert.True((await worker.CallResponseAsync("inverter.read", new { remoteId = "plant_14_1_1" })).TryGetProperty("error", out _));
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("2024-10-27T00:00:00Z", "2024-10-27T01:00:00Z", true)]
    [InlineData(null, null, false)]
    public async Task SungrowAmbiguousOrCurrentDayHistoryCannotClaimComplete(string? start, string? end, bool expectError)
    {
        await using var server = new MockVendorServer(SungrowSource, request =>
        {
            ValidateSungrow(request);
            if (request.Path.EndsWith("queryPowerStationList")) return SungrowPlants();
            Assert.EndsWith("getDeviceListByPsId", request.Path);
            return SungrowDevices(request);
        });
        await using var worker = await ProviderWorkerSession.StartAsync("SungrowCloud", "sungrow.cloud", server, SungrowValues, SungrowSecrets, ["https://gateway.isolarcloud.eu"]);
        var now = DateTimeOffset.UtcNow;
        var response = await worker.CallResponseAsync("inverter.history", new { remoteId = "plant_14_1_1", start = start is null ? now : DateTimeOffset.Parse(start),
            end = end is null ? now.AddMinutes(1) : DateTimeOffset.Parse(end) });
        if (expectError) Assert.True(response.TryGetProperty("error", out _));
        else Assert.False(response.GetProperty("result").GetProperty("isComplete").GetBoolean());
        Assert.DoesNotContain(server.Requests, r => r.Path.EndsWith("getDevicePointMinuteDataList"));
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task HuaweiReusesHeaderLoginTokenSeparatesMeterAndPreservesUnknownLiveTimestamps()
    {
        await using var server = new MockVendorServer(HuaweiSource, request => HuaweiResponse(request));
        await using var worker = await ProviderWorkerSession.StartAsync("HuaweiFusionSolar", "huawei.fusionsolar", server, HuaweiValues, HuaweiSecrets, ["https://eu5.fusionsolar.huawei.com"]);
        Assert.True((await worker.CallAsync("test", new { })).GetProperty("success").GetBoolean());
        var device = Assert.Single((await worker.CallAsync("discover", new { })).EnumerateArray());
        Assert.True(device.GetProperty("metadata").GetProperty("capabilities").GetProperty("hasBattery").GetBoolean());
        var telemetry = await worker.CallAsync("inverter.read", new { remoteId = "1" });
        AssertMeasurement(telemetry, "solarPower", 5000, null);
        AssertMeasurement(telemetry, "batterySoc", 75, null);
        AssertMeasurement(telemetry, "batteryPower", -1200, null);
        AssertMeasurement(telemetry, "gridPower", -450, null);
        AssertMeasurement(telemetry, "batteryVoltage", 350, null);
        Assert.Equal((int)ProviderMeasurementQuality.Missing, telemetry.GetProperty("loadPower").GetProperty("quality").GetInt32());
        var history = await worker.CallAsync("inverter.history", History("1"));
        Assert.True(history.GetProperty("isComplete").GetBoolean());
        Assert.Equal(-450, Assert.Single(history.GetProperty("samples").EnumerateArray()).GetProperty("powerWatts").GetInt32());
        Assert.Single(server.Requests.Where(x => x.Path == "/thirdData/login"));
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task HuaweiOAuthModeUsesBearerWithoutCallingApiAccountLogin()
    {
        await using var server = new MockVendorServer(HuaweiSource, request => HuaweiResponse(request, oauth: true));
        await using var worker = await ProviderWorkerSession.StartAsync("HuaweiFusionSolar", "huawei.fusionsolar", server,
            new { authMode = "accessToken", gridPositiveDirection = "import", batteryPositiveDirection = "discharge" }, Token, ["https://eu5.fusionsolar.huawei.com"]);
        var telemetry = await worker.CallAsync("inverter.read", new { remoteId = "1" });
        AssertMeasurement(telemetry, "gridPower", 450, null);
        Assert.DoesNotContain(server.Requests, x => x.Path == "/thirdData/login");
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task HuaweiAmbiguousPlantTopologyDoesNotBorrowBatteryOrGridFromAnotherInverter()
    {
        await using var server = new MockVendorServer(HuaweiSource, request => HuaweiResponse(request, twoInverters: true));
        await using var worker = await ProviderWorkerSession.StartAsync("HuaweiFusionSolar", "huawei.fusionsolar", server, HuaweiValues, HuaweiSecrets, ["https://eu5.fusionsolar.huawei.com"]);
        var devices = await worker.CallAsync("discover", new { });
        Assert.Equal(2, devices.GetArrayLength());
        foreach (var device in devices.EnumerateArray())
            Assert.False(device.GetProperty("metadata").GetProperty("capabilities").GetProperty("hasBattery").GetBoolean());
        var telemetry = await worker.CallAsync("inverter.read", new { remoteId = "1" });
        foreach (var name in new[] { "batterySoc", "batteryPower", "gridPower" })
            Assert.Equal((int)ProviderMeasurementQuality.Missing, telemetry.GetProperty(name).GetProperty("quality").GetInt32());
        Assert.DoesNotContain(server.Requests, x => x.Path.EndsWith("getDevRealKpi") && x.Json.GetProperty("devIds").GetString() != "1");
        Assert.True((await worker.CallResponseAsync("inverter.history", History("1"))).TryGetProperty("error", out _));
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task HuaweiRejectedLoginDoesNotReplayPasswordAndNeverClaimsConnectionSuccess()
    {
        await using var server = new MockVendorServer(HuaweiSource, request =>
        {
            Assert.Equal("/thirdData/login", request.Path);
            return FixtureResponse.Json(new { success = false, failCode = 20003, data = (object?)null });
        });
        await using var worker = await ProviderWorkerSession.StartAsync("HuaweiFusionSolar", "huawei.fusionsolar", server, HuaweiValues, HuaweiSecrets, ["https://eu5.fusionsolar.huawei.com"]);
        for (var call = 0; call < 2; call++)
        {
            var response = await worker.CallResponseAsync("test", new { });
            Assert.True(response.TryGetProperty("error", out _));
            Assert.DoesNotContain("fixture-system-code", response.GetRawText());
        }
        Assert.Single(server.Requests);
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("SolisCloud", "solis.cloud", "https://api-oauth2.soliscloud.com")]
    [InlineData("SungrowCloud", "sungrow.cloud", "https://gateway.isolarcloud.eu")]
    [InlineData("HuaweiFusionSolar", "huawei.fusionsolar", "https://eu5.fusionsolar.huawei.com")]
    public async Task MissingAuthorizationFailsReadinessWithoutAnyVendorRequest(string suffix, string providerId, string origin)
    {
        await using var server = new MockVendorServer(SolisSource, _ => throw new InvalidOperationException("No HTTP should occur."));
        await using var worker = await ProviderWorkerSession.StartAsync(suffix, providerId, server, new { baseUrl = origin }, allowedOrigins: [origin]);
        var result = await worker.CallAsync("test", new { });
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("authorization_required", result.GetProperty("code").GetString());
        Assert.Empty(server.Requests);
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("SolisCloud")]
    [InlineData("SungrowCloud")]
    [InlineData("HuaweiFusionSolar")]
    public void InverterDescriptorsAreValidAndIdentifyWizardKindWithoutNewCoreContracts(string suffix)
    {
        var root = RepositoryRoot();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "integrations", "SolarManagement.Providers." + suffix, "manifest.json")));
        var descriptor = doc.RootElement.GetProperty("descriptor").Deserialize<IntegrationProviderDescriptor>(IntegrationJson.Options)!;
        IntegrationDescriptorValidator.Validate(descriptor);
        Assert.Equal("inverter", descriptor.UiLayout!.Steps[0].Id);
        Assert.DoesNotContain("oauth", descriptor.Actions);
    }

    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "integrations"))) return current.FullName;
        throw new DirectoryNotFoundException("Repository fixtures are unavailable.");
    }
    private static object History(string remoteId) => new { remoteId, start = Collected, end = Collected.AddHours(1) };
    private static void AssertMeasurement(JsonElement reading, string name, decimal expected, DateTimeOffset? time)
    {
        var measurement = reading.GetProperty(name);
        Assert.Equal(expected, measurement.GetProperty("value").GetDecimal());
        Assert.Equal((int)ProviderMeasurementQuality.Good, measurement.GetProperty("quality").GetInt32());
        if (time.HasValue) Assert.Equal(time.Value, measurement.GetProperty("observedAt").GetDateTimeOffset());
        else Assert.Equal(JsonValueKind.Null, measurement.GetProperty("observedAt").ValueKind);
    }
    private static void ValidateSolis(FixtureRequest request)
    {
        Assert.Equal("POST", request.Method);
        Assert.Equal("api-oauth2.soliscloud.com", request.Uri.Host);
        Assert.StartsWith("/api/access_data/", request.Path);
        Assert.Equal("Bearer fixture-access-token", request.Header("Authorization"));
        Assert.StartsWith("application/json", request.Header("Content-Type"));
    }
    private static FixtureResponse SolisData(object data) => FixtureResponse.Json(new { success = true, code = "0", data });
    private static FixtureResponse SolisInventory() => SolisData(new { page = new { records = new[] { new { id = 1, sn = "solis-sn", stationId = "station-1", productModel = "2" } } } });
    private static void ValidateSungrow(FixtureRequest request)
    {
        Assert.Equal("POST", request.Method);
        Assert.Equal("gateway.isolarcloud.eu", request.Uri.Host);
        Assert.Equal("Bearer fixture-access-token", request.Header("Authorization"));
        Assert.Equal("fixture-access-key", request.Header("x-access-key"));
        Assert.Equal("fixture-app-key", request.Json.GetProperty("appkey").GetString());
        Assert.Equal("_en_US", request.Json.GetProperty("lang").GetString());
    }
    private static FixtureResponse SungrowData(object data) => FixtureResponse.Json(new { result_code = "1", result_data = data });
    private static FixtureResponse SungrowPlants() => SungrowData(new { row_count = "1", pageList = new[] { new { ps_id = 1, ps_name = "Plant", ps_type = 5 } } });
    private static FixtureResponse SungrowDevices(FixtureRequest request)
    {
        Assert.Equal("1", request.Json.GetProperty("ps_id").GetString());
        Assert.Equal(14, request.Json.GetProperty("device_type_list")[0].GetInt32());
        return SungrowData(new { row_count = 1, pageList = new[] { new { ps_key = "plant_14_1_1", device_sn = "sg-sn", device_type = 14,
            device_name = "Hybrid inverter", device_model_code = "SH10T" } } });
    }
    private static object SungrowPoint() => new { ps_key = "plant_14_1_1", device_sn = "sg-sn", device_time = "20240725120000",
        p13003 = "5000", p13141 = "72", p13150 = "200", p13126 = "0", p13138 = "350", p13139 = "-3", p13143 = "27",
        p13149 = "300", p13121 = "800", p13119 = "3000" };
    private static FixtureResponse HuaweiResponse(FixtureRequest request, bool oauth = false, bool twoInverters = false)
    {
        Assert.Equal("POST", request.Method);
        Assert.Equal("eu5.fusionsolar.huawei.com", request.Uri.Host);
        if (request.Path == "/thirdData/login")
        {
            Assert.False(oauth);
            Assert.Equal("fixture-api-user", request.Json.GetProperty("userName").GetString());
            Assert.Equal("fixture-system-code", request.Json.GetProperty("systemCode").GetString());
            return HuaweiData(null) with { Headers = new Dictionary<string, string> { ["XSRF-TOKEN"] = "fixture-xsrf" } };
        }
        if (oauth) Assert.Equal("Bearer fixture-access-token", request.Header("Authorization"));
        else Assert.Equal("fixture-xsrf", request.Header("XSRF-TOKEN"));
        if (request.Path == "/thirdData/stations")
        {
            Assert.Equal(1, request.Json.GetProperty("pageNo").GetInt32());
            return HuaweiData(new { pageCount = 1, pageNo = 1, pageSize = 100, total = 1,
                list = new[] { new { plantCode = "NE=plant", plantName = "Plant" } } });
        }
        if (request.Path == "/thirdData/getDevList")
        {
            Assert.Equal("NE=plant", request.Json.GetProperty("stationCodes").GetString());
            var devices = new List<object> { new { id = 1, devTypeId = 38, stationCode = "NE=plant", devName = "Inverter", esnCode = "hw-sn", model = "SUN2000" },
                new { id = 2, devTypeId = 39, stationCode = "NE=plant", devName = "Battery" }, new { id = 3, devTypeId = 17, stationCode = "NE=plant", devName = "Grid meter" } };
            if (twoInverters) devices.Add(new { id = 4, devTypeId = 38, stationCode = "NE=plant", devName = "Inverter 2" });
            return HuaweiData(devices);
        }
        if (request.Path == "/thirdData/getDevRealKpi")
        {
            var id = request.Json.GetProperty("devIds").GetString();
            var type = request.Json.GetProperty("devTypeId").GetInt32();
            object point = id switch { "1" when type == 38 => new { mppt_power = 5, active_power = 99 },
                "2" when type == 39 => new { battery_soc = 75, ch_discharge_power = 1200, busbar_u = 350 },
                "3" when type == 17 => new { active_power = 450 }, _ => throw new InvalidDataException("Wrong Huawei device/type") };
            // currentTime is intentionally newer; it must not become an observed timestamp.
            return FixtureResponse.Json(new { success = true, failCode = 0, data = new[] { new { devId = int.Parse(id!), dataItemMap = point } },
                @params = new { currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } });
        }
        Assert.Equal("/thirdData/getDevHistoryKpi", request.Path);
        Assert.Equal("3", request.Json.GetProperty("devIds").GetString());
        Assert.Equal(17, request.Json.GetProperty("devTypeId").GetInt32());
        Assert.Equal(Collected.ToUnixTimeMilliseconds(), request.Json.GetProperty("startTime").GetInt64());
        return HuaweiData(new[] { new { devId = 3, collectTime = Collected.ToUnixTimeMilliseconds(), dataItemMap = new { active_power = 450 } } });
    }
    private static FixtureResponse HuaweiData(object? data) => FixtureResponse.Json(new { success = true, failCode = 0, data });
}

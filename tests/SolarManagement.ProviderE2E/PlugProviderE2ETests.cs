using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SolarManagement.ProviderE2E;

/// <summary>Framed-process E2E against official REST contracts; no real cloud credentials are used.</summary>
public sealed class PlugProviderE2ETests
{
    [Theory]
    [InlineData("TuyaCloud")]
    [InlineData("EWeLinkCloud")]
    [InlineData("AqaraCloud")]
    [InlineData("NetatmoControl")]
    public async Task AuthenticatedInventoryDiscoveryReadAndBothCommands(string suffix)
    {
        var fixture = new PlugFixture(suffix);
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        Assert.Contains("socket.set", worker.Handshake.GetProperty("operations").EnumerateArray().Select(x => x.GetString()));
        var connection = await worker.CallAsync("test", new { });
        Assert.True(connection.GetProperty("success").GetBoolean());
        var discovered = Assert.Single((await worker.CallAsync("discover", new { })).EnumerateArray());
        Assert.Equal("plug-1", discovered.GetProperty("remoteId").GetString());
        Assert.Equal(fixture.Channel, discovered.GetProperty("channel").GetString());
        Assert.True(discovered.GetProperty("metadata").GetProperty("capabilities").GetProperty("canSwitch").GetBoolean());
        Assert.Equal(connection.GetProperty("accountIdentity").GetString(), discovered.GetProperty("accountIdentity").GetString());
        if (suffix is "TuyaCloud" or "EWeLinkCloud") Assert.False(string.IsNullOrEmpty(discovered.GetProperty("accountIdentity").GetString()));
        else Assert.Equal(JsonValueKind.Null, discovered.GetProperty("accountIdentity").ValueKind);
        var inventory = Assert.Single((await worker.CallAsync("socket.inventory", new { })).EnumerateArray());
        Assert.True(inventory.GetProperty("isOn").GetBoolean());
        var reading = await worker.CallAsync("socket.read", new { remoteId = "plug-1", channel = fixture.Channel });
        if (suffix == "AqaraCloud") Assert.Equal(JsonValueKind.Null, reading.GetProperty("currentPowerWatts").ValueKind);
        else Assert.Equal(123, reading.GetProperty("currentPowerWatts").GetInt32());
        foreach (var desired in new[] { false, true })
        {
            var commandId = Guid.NewGuid().ToString("D");
            var result = await worker.CallAsync("socket.set", new { remoteId = "plug-1", channel = fixture.Channel, commandId, isOn = desired });
            Assert.Equal("Acknowledged", result.GetProperty("status").GetString());
            Assert.Equal(commandId, result.GetProperty("commandId").GetString());
            Assert.Equal(desired, result.GetProperty("observedState").GetProperty("isOn").GetBoolean());
        }
        Assert.Equal(2, fixture.CommandCount);
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("TuyaCloud")]
    [InlineData("EWeLinkCloud")]
    [InlineData("AqaraCloud")]
    [InlineData("NetatmoControl")]
    public async Task MissingValuesRemainUnknownAndCannotSelectUnknownChannels(string suffix)
    {
        var fixture = new PlugFixture(suffix) { MissingValues = true };
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        var reading = await worker.CallAsync("socket.read", new { remoteId = "plug-1", channel = fixture.Channel });
        foreach (var property in new[] { "isOn", "online", "currentPowerWatts", "observedAt" }) Assert.Equal(JsonValueKind.Null, reading.GetProperty(property).ValueKind);
        var failure = await worker.CallResponseAsync("socket.set", new { remoteId = "plug-1", channel = "unknown", commandId = Guid.NewGuid(), isOn = true });
        Assert.Equal("Rejected", failure.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(0, fixture.CommandCount);
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("TuyaCloud")]
    [InlineData("EWeLinkCloud")]
    [InlineData("AqaraCloud")]
    [InlineData("NetatmoControl")]
    public async Task InvalidCredentialsOrOversizedIncompleteInventoryFailClosed(string suffix)
    {
        var fixture = new PlugFixture(suffix) { AuthenticationFailure = true };
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        Assert.True((await worker.CallResponseAsync("test", new { })).TryGetProperty("error", out var error));
        Assert.Equal("Integration operation failed.", error.GetProperty("message").GetString());
        fixture.AuthenticationFailure = false;
        fixture.ExcessiveInventory = true;
        Assert.True((await worker.CallResponseAsync("socket.inventory", new { })).TryGetProperty("error", out _));
        Assert.Equal(0, fixture.CommandCount);
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("TuyaCloud")]
    [InlineData("EWeLinkCloud")]
    [InlineData("AqaraCloud")]
    [InlineData("NetatmoControl")]
    public async Task VendorAcceptanceWithoutObservedTransitionRemainsUncertain(string suffix)
    {
        var fixture = new PlugFixture(suffix) { ApplyCommands = false };
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        var result = await worker.CallAsync("socket.set", new { remoteId = "plug-1", channel = fixture.Channel, commandId = Guid.NewGuid(), isOn = false });
        Assert.Equal("Uncertain", result.GetProperty("status").GetString());
        Assert.True(result.GetProperty("observedState").GetProperty("isOn").GetBoolean());
        Assert.Equal(1, fixture.CommandCount);
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("TuyaCloud")]
    [InlineData("EWeLinkCloud")]
    [InlineData("AqaraCloud")]
    [InlineData("NetatmoControl")]
    public async Task VendorCommandErrorNeverAcknowledges(string suffix)
    {
        var fixture = new PlugFixture(suffix) { RejectCommands = true };
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        Assert.True((await worker.CallResponseAsync("socket.set", new { remoteId = "plug-1", channel = fixture.Channel, commandId = Guid.NewGuid(), isOn = false })).TryGetProperty("error", out _));
        Assert.True(fixture.On);
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("TuyaCloud")]
    [InlineData("EWeLinkCloud")]
    [InlineData("AqaraCloud")]
    [InlineData("NetatmoControl")]
    public async Task OfflineSocketDoesNotSendCommand(string suffix)
    {
        var fixture = new PlugFixture(suffix) { Offline = true };
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        var result = await worker.CallAsync("socket.set", new { remoteId = "plug-1", channel = fixture.Channel, commandId = Guid.NewGuid(), isOn = false });
        Assert.Equal("Rejected", result.GetProperty("status").GetString());
        Assert.Equal(0, fixture.CommandCount);
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("TuyaCloud")]
    [InlineData("EWeLinkCloud")]
    [InlineData("AqaraCloud")]
    [InlineData("NetatmoControl")]
    public async Task InjectedTransportCannotBypassOfficialOriginValidation(string suffix)
    {
        var fixture = new PlugFixture(suffix);
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var worker = await ProviderWorkerSession.StartAsync(suffix, fixture.ProviderId, server,
                new { endpoint = "https://attacker.example/api", userId = "user-1" }, fixture.Secrets, ["https://attacker.example"]);
        });
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task EWeLinkMultiRelayUsesOnlySelectedOutletAndNeverThreePhaseAggregate()
    {
        var fixture = new PlugFixture("EWeLinkCloud") { Multi = true };
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        var discovered = (await worker.CallAsync("discover", new { })).EnumerateArray().ToArray();
        Assert.Equal(3, discovered.Length);
        Assert.Equal(new[] { "0", "1", "2" }, discovered.Select(d => d.GetProperty("channel").GetString()));
        Assert.All(discovered, d => Assert.Equal(JsonValueKind.Null, d.GetProperty("metadata").GetProperty("provider").GetProperty("phases").ValueKind));
        var result = await worker.CallAsync("socket.set", new { remoteId = "plug-1", channel = "1", commandId = Guid.NewGuid(), isOn = false });
        Assert.Equal("Acknowledged", result.GetProperty("status").GetString());
        Assert.Equal(new[] { true, false, true }, (await worker.CallAsync("socket.inventory", new { })).EnumerateArray().Select(d => d.GetProperty("isOn").GetBoolean()));
        Assert.All((await worker.CallAsync("socket.inventory", new { })).EnumerateArray(), d => Assert.Equal(JsonValueKind.Null, d.GetProperty("currentPowerWatts").ValueKind));
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task TuyaSpecificationControlsScaleAndUnits()
    {
        var fixture = new PlugFixture("TuyaCloud") { PowerUnit = "kWh" };
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        var discovered = Assert.Single((await worker.CallAsync("discover", new { })).EnumerateArray());
        Assert.False(discovered.GetProperty("metadata").GetProperty("capabilities").GetProperty("canMeasurePower").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await worker.CallAsync("socket.read", new { remoteId = "plug-1", channel = fixture.Channel })).GetProperty("currentPowerWatts").ValueKind);
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task TuyaFullInventoryRequiresFinalEmptyPage()
    {
        var fixture = new PlugFixture("TuyaCloud") { PagedInventoryCount = 200 };
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        Assert.Single((await worker.CallAsync("discover", new { })).EnumerateArray());
        Assert.Equal(Enumerable.Range(1, 5).Select(page => "?page_no=" + page + "&page_size=50"),
            server.Requests.Where(r => r.Path == "/v1.0/users/user-1/devices").Select(r => r.Uri.Query));
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task EWeLinkNamedButUndocumentedUiidNeverBecomesSwitchable()
    {
        var fixture = new PlugFixture("EWeLinkCloud") { Multi = true, UiidOverride = 140 };
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        Assert.Empty((await worker.CallAsync("discover", new { })).EnumerateArray());
        Assert.Equal("Rejected", (await worker.CallAsync("socket.set", new { remoteId = "plug-1", channel = "1", commandId = Guid.NewGuid(), isOn = false })).GetProperty("status").GetString());
        Assert.Equal(0, fixture.CommandCount);
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task AqaraMismatchedResourceModelCannotSendCommand()
    {
        var fixture = new PlugFixture("AqaraCloud") { ResourceModelMismatch = true };
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        Assert.True((await worker.CallResponseAsync("socket.set", new { remoteId = "plug-1", channel = fixture.Channel, commandId = Guid.NewGuid(), isOn = false })).TryGetProperty("error", out _));
        Assert.Equal(0, fixture.CommandCount);
        server.AssertNoProtocolFailures();
    }

    [Theory]
    [InlineData("TuyaCloud")]
    [InlineData("EWeLinkCloud")]
    public async Task MissingAuthoritativeProfileCannotClaimVerifiedAccount(string suffix)
    {
        var fixture = new PlugFixture(suffix) { ProfileUserId = null };
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        Assert.True((await worker.CallResponseAsync("test", new { })).TryGetProperty("error", out _));
        Assert.True((await worker.CallResponseAsync("discover", new { })).TryGetProperty("error", out _));
        Assert.Equal(0, fixture.CommandCount);
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task TuyaProfileUidMustMatchAuthorizedRequestedUser()
    {
        var fixture = new PlugFixture("TuyaCloud") { ProfileUserId = "different-user" };
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        await using var worker = await fixture.StartAsync(server);
        Assert.True((await worker.CallResponseAsync("test", new { })).TryGetProperty("error", out _));
        Assert.Equal(0, fixture.CommandCount);
        server.AssertNoProtocolFailures();
    }

    [Fact]
    public async Task EWeLinkAccountIdentityFollowsProfileAndSurvivesTokenRenewal()
    {
        var fixture = new PlugFixture("EWeLinkCloud");
        await using var server = new MockVendorServer(fixture.Source, fixture.Handle);
        string? identity;
        await using (var original = await fixture.StartAsync(server))
            identity = (await original.CallAsync("test", new { })).GetProperty("accountIdentity").GetString();
        fixture.AccessToken = "renewed-token";
        var secrets = fixture.Secrets;
        secrets["accessToken"] = fixture.AccessToken;
        await using var renewed = await ProviderWorkerSession.StartAsync("EWeLinkCloud", fixture.ProviderId, server,
            new { endpoint = fixture.Endpoint }, secrets, [new Uri(fixture.Endpoint).GetLeftPart(UriPartial.Authority)]);
        Assert.Equal(identity, (await renewed.CallAsync("test", new { })).GetProperty("accountIdentity").GetString());
        fixture.ProfileUserId = "another-user";
        Assert.NotEqual(identity, (await renewed.CallAsync("test", new { })).GetProperty("accountIdentity").GetString());
        Assert.Equal(0, fixture.CommandCount);
        server.AssertNoProtocolFailures();
    }

    private sealed class PlugFixture(string suffix)
    {
        public bool On = true, AuthenticationFailure, ExcessiveInventory, MissingValues, Offline, RejectCommands, Multi;
        public bool ResourceModelMismatch;
        public bool ApplyCommands = true;
        public int CommandCount, PagedInventoryCount;
        public int? UiidOverride;
        public string? ProfileUserId = "user-1";
        public string AccessToken = "token-1";
        public string PowerUnit = "W";
        private readonly bool[] _channels = [true, true, true];
        public string Channel => suffix switch { "TuyaCloud" => "switch_1", "EWeLinkCloud" => "0", "AqaraCloud" => "4.1.85", _ => null! };
        public string ProviderId => suffix switch { "TuyaCloud" => "tuya.cloud", "EWeLinkCloud" => "ewelink.cloud", "AqaraCloud" => "aqara.cloud", _ => "netatmo.control" };
        public string Endpoint => suffix switch { "TuyaCloud" => "https://openapi.tuyaeu.com", "EWeLinkCloud" => "https://eu-apia.coolkit.cc", "AqaraCloud" => "https://open-ger.aqara.com/v3.0/open/api", _ => "https://api.netatmo.com/api" };
        public string Source => suffix switch
        {
            "TuyaCloud" => "https://developer.tuya.com/en/docs/iot/new-singnature?id=Kbw0q34cs2e5g",
            "EWeLinkCloud" => "https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md",
            "AqaraCloud" => "https://opendoc.aqara.com/en/docs/developmanual/apiIntroduction/signGenerationRules.html",
            _ => "https://dev.netatmo.com/apidocumentation/control"
        };
        public Dictionary<string, string> Secrets => suffix switch
        {
            "TuyaCloud" => new() { ["accessId"] = "client-id", ["accessSecret"] = "client-secret" },
            "EWeLinkCloud" => new() { ["appId"] = "app-id", ["accessToken"] = "token-1" },
            "AqaraCloud" => new() { ["appId"] = "app-id", ["keyId"] = "key-id", ["appKey"] = "app-key", ["accessToken"] = "token-1" },
            _ => new() { ["accessToken"] = "token-1" }
        };
        public Task<ProviderWorkerSession> StartAsync(MockVendorServer server)
            => ProviderWorkerSession.StartAsync(suffix, ProviderId, server, new { endpoint = Endpoint, userId = "user-1" }, Secrets, [new Uri(Endpoint).GetLeftPart(UriPartial.Authority)]);
        private object? State => MissingValues ? null : On;
        private object? Reachable => MissingValues ? null : !Offline;
        private static FixtureResponse TuyaResult(object? value) => FixtureResponse.Json(new { success = true, result = value });
        private static FixtureResponse AqaraResult(object? value) => FixtureResponse.Json(new { code = 0, result = value });
        private static FixtureResponse EWeLinkResult(object? value) => FixtureResponse.Json(new { error = 0, data = value });
        private static FixtureResponse NetatmoResult(object? value) => FixtureResponse.Json(new { status = "ok", body = value });
        public FixtureResponse Handle(FixtureRequest request)
        {
            Assert.Equal("https", request.Uri.Scheme);
            Assert.Equal(new Uri(Endpoint).Host, request.Uri.Host);
            if (suffix == "TuyaCloud") return Tuya(request);
            if (suffix == "AqaraCloud") return Aqara(request);
            Assert.Equal("Bearer " + AccessToken, request.Header("Authorization"));
            if (suffix == "EWeLinkCloud") return EWeLink(request);
            return Netatmo(request);
        }
        private FixtureResponse Tuya(FixtureRequest request)
        {
            Assert.Equal("client-id", request.Header("client_id"));
            Assert.Equal("HMAC-SHA256", request.Header("sign_method"));
            var token = request.Path == "/v1.0/token" ? "" : "token-1";
            Assert.Equal(token, request.Header("access_token"));
            Assert.True(long.TryParse(request.Header("t"), CultureInfo.InvariantCulture, out _));
            var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Body))).ToLowerInvariant();
            var canonical = string.Join("\n", request.Method, bodyHash, "", request.Uri.PathAndQuery);
            var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("client-secret"), Encoding.UTF8.GetBytes("client-id" + token + request.Header("t") + canonical)));
            Assert.Equal(expected, request.Header("sign"));
            if (AuthenticationFailure) return FixtureResponse.Raw("{\"success\":false,\"code\":1010}");
            if (request.Path == "/v1.0/token")
            {
                Assert.Equal("GET", request.Method); Assert.Equal("?grant_type=1", request.Uri.Query);
                return TuyaResult(new { access_token = "token-1", expire_time = 7200 });
            }
            if (request.Path == "/v1.0/users/user-1/infos")
            {
                Assert.Equal("GET", request.Method); Assert.Equal("", request.Uri.Query);
                return TuyaResult(new { uid = ProfileUserId });
            }
            if (request.Path == "/v1.0/users/user-1/devices")
            {
                if (PagedInventoryCount > 0)
                {
                    var page = int.Parse(request.Uri.Query["?page_no=".Length..].Split('&')[0], CultureInfo.InvariantCulture);
                    Assert.Equal("?page_no=" + page + "&page_size=50", request.Uri.Query);
                    Assert.InRange(page, 1, 5);
                    return TuyaResult(Enumerable.Range(0, PagedInventoryCount).Skip((page - 1) * 50).Take(50)
                        .Select(i => new { id = i == 0 ? "plug-1" : "other-" + i, category = i == 0 ? "cz" : "wg2", online = Reachable, product_id = "product-1" }).ToArray());
                }
                Assert.Equal("?page_no=1&page_size=50", request.Uri.Query);
                return TuyaResult(Enumerable.Range(0, ExcessiveInventory ? 201 : 1).Select(i => new { id = i == 0 ? "plug-1" : "plug-" + (i + 1), category = "cz", name = "Kitchen", online = Reachable, product_id = "product-1" }).ToArray());
            }
            if (request.Path == "/v1.2/iot-03/devices/plug-1/specification")
                return TuyaResult(new { category = "cz", functions = new[] { new { code = "switch_1", type = "Boolean", values = "{}" } }, status = new[] { new { code = "cur_power", type = "Integer", values = JsonSerializer.Serialize(new { unit = PowerUnit, scale = 1 }) } } });
            if (request.Path == "/v1.0/iot-03/devices/plug-1/status")
                return TuyaResult(new[] { new { code = "switch_1", value = State }, new { code = "cur_power", value = MissingValues ? null : (object)1234 } });
            Assert.Equal("POST", request.Method); Assert.Equal("/v1.0/iot-03/devices/plug-1/commands", request.Path);
            var command = Assert.Single(request.Json.GetProperty("commands").EnumerateArray());
            Assert.Equal("switch_1", command.GetProperty("code").GetString());
            CommandCount++;
            if (!RejectCommands && ApplyCommands) On = command.GetProperty("value").GetBoolean();
            return RejectCommands ? FixtureResponse.Raw("{\"success\":false,\"code\":2001}") : TuyaResult(true);
        }
        private FixtureResponse EWeLink(FixtureRequest request)
        {
            Assert.Equal("app-id", request.Header("X-CK-Appid"));
            Assert.Matches("^[a-zA-Z0-9]{8}$", request.Header("X-CK-Nonce"));
            if (AuthenticationFailure) return FixtureResponse.Raw("{\"error\":401,\"data\":{}}");
            if (request.Path == "/v2/user/profile")
            {
                Assert.Equal("GET", request.Method); Assert.Equal("", request.Uri.Query);
                return EWeLinkResult(new { user = new { apikey = ProfileUserId }, region = "eu" });
            }
            if (request.Path == "/v2/device/thing")
            {
                Assert.Equal("GET", request.Method); Assert.Equal("?num=30", request.Uri.Query);
                object parameters = Multi ? new { switches = _channels.Select((value, index) => new { outlet = index, @switch = value ? "on" : "off" }).ToArray(), power = "999.0" }
                    : new { @switch = MissingValues ? null : On ? "on" : "off", power = MissingValues ? null : "123.4" };
                return EWeLinkResult(new { total = ExcessiveInventory ? 31 : 1, thingList = new[] { new { itemType = 1, itemData = new { deviceid = "plug-1", name = "Kitchen", online = Reachable, extra = new { uiid = UiidOverride ?? (Multi ? 3 : 5) }, @params = parameters } } } });
            }
            Assert.Equal("POST", request.Method); Assert.Equal("/v2/device/thing/status", request.Path);
            Assert.Equal(1, request.Json.GetProperty("type").GetInt32()); Assert.Equal("plug-1", request.Json.GetProperty("id").GetString());
            var args = request.Json.GetProperty("params");
            if (Multi)
            {
                var s = Assert.Single(args.GetProperty("switches").EnumerateArray());
                Assert.Equal(1, s.GetProperty("outlet").GetInt32());
                if (!RejectCommands && ApplyCommands) _channels[1] = s.GetProperty("switch").GetString() == "on";
            }
            else if (!RejectCommands && ApplyCommands) On = args.GetProperty("switch").GetString() == "on";
            CommandCount++;
            return RejectCommands ? FixtureResponse.Raw("{\"error\":4002,\"data\":{}}") : EWeLinkResult(new { });
        }
        private FixtureResponse Aqara(FixtureRequest request)
        {
            Assert.Equal("POST", request.Method); Assert.Equal("/v3.0/open/api", request.Path);
            Assert.Equal("app-id", request.Header("Appid")); Assert.Equal("key-id", request.Header("Keyid")); Assert.Equal("token-1", request.Header("Accesstoken"));
            var signing = string.Join("&", new[] { "Accesstoken", "Appid", "Keyid", "Nonce", "Time" }.Order(StringComparer.Ordinal).Select(key => key + "=" + request.Header(key))) + "app-key";
            Assert.Equal(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(signing.ToLowerInvariant()))).ToLowerInvariant(), request.Header("Sign"));
            if (AuthenticationFailure) return FixtureResponse.Raw("{\"code\":102,\"result\":null}");
            var intent = request.Json.GetProperty("intent").GetString(); var data = request.Json.GetProperty("data");
            if (intent == "query.device.info")
            {
                Assert.Equal(1, data.GetProperty("pageNum").GetInt32()); Assert.Equal(50, data.GetProperty("pageSize").GetInt32());
                return AqaraResult(new { totalCount = ExcessiveInventory ? 201 : 1, data = new[] { new { did = "plug-1", model = "lumi.plug.maeu01", deviceName = "Kitchen", state = MissingValues ? null : Offline ? (int?)0 : 1 } } });
            }
            if (intent == "query.resource.info")
            {
                Assert.Equal("lumi.plug.maeu01", data.GetProperty("model").GetString());
                var model = ResourceModelMismatch ? "lumi.plug.other" : "lumi.plug.maeu01";
                return AqaraResult(new[] { new { resourceId = "4.1.85", enums = "0,1", access = 7, unit = 1, model }, new { resourceId = "0.12.85", enums = "", access = 3, unit = 1, model } });
            }
            if (intent == "query.resource.value")
            {
                var resources = Assert.Single(data.GetProperty("resources").EnumerateArray());
                Assert.Equal("plug-1", resources.GetProperty("subjectId").GetString());
                Assert.Equal("4.1.85", Assert.Single(resources.GetProperty("resourceIds").EnumerateArray()).GetString());
                return AqaraResult(new[] { new { subjectId = "plug-1", resourceId = "4.1.85", value = MissingValues ? null : On ? "1" : "0", timeStamp = MissingValues ? null : (long?)1791100800000 } });
            }
            Assert.Equal("write.resource.device", intent);
            var subject = Assert.Single(data.EnumerateArray()); Assert.Equal("plug-1", subject.GetProperty("subjectId").GetString());
            var command = Assert.Single(subject.GetProperty("resources").EnumerateArray()); Assert.Equal("4.1.85", command.GetProperty("resourceId").GetString());
            CommandCount++;
            if (!RejectCommands && ApplyCommands) On = command.GetProperty("value").GetString() == "1";
            return RejectCommands ? FixtureResponse.Raw("{\"code\":302,\"result\":null}") : AqaraResult("");
        }
        private FixtureResponse Netatmo(FixtureRequest request)
        {
            if (AuthenticationFailure) return FixtureResponse.Raw("{\"error\":{\"code\":2}}", 401);
            if (request.Path == "/api/homesdata")
            {
                Assert.Equal("GET", request.Method);
                return NetatmoResult(new { homes = Enumerable.Range(0, ExcessiveInventory ? 21 : 1).Select(i => new { id = "home-1", modules = new[] { new { id = "plug-1", type = "NLPM", name = "Kitchen", bridge = "gateway-1" } } }).ToArray() });
            }
            if (request.Path == "/api/homestatus")
            {
                Assert.Equal("GET", request.Method); Assert.Equal("?home_id=home-1", request.Uri.Query);
                return NetatmoResult(new { home = new { id = "home-1", modules = new[] { new { id = "plug-1", type = "NLPM", on = State, reachable = Reachable, power = MissingValues ? null : (int?)123, last_seen = MissingValues ? null : (long?)1791100800 } } } });
            }
            Assert.Equal("POST", request.Method); Assert.Equal("/api/setstate", request.Path);
            var home = request.Json.GetProperty("home"); Assert.Equal("home-1", home.GetProperty("id").GetString());
            var command = Assert.Single(home.GetProperty("modules").EnumerateArray());
            Assert.Equal("plug-1", command.GetProperty("id").GetString()); Assert.Equal("gateway-1", command.GetProperty("bridge").GetString());
            CommandCount++;
            if (!RejectCommands && ApplyCommands) On = command.GetProperty("on").GetBoolean();
            return NetatmoResult(new { errors = RejectCommands ? new object[] { new { id = "plug-1", code = 1 } } : [] });
        }
    }
}

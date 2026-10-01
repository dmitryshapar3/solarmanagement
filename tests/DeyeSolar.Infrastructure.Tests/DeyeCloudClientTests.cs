using System.Net;
using DeyeSolar.Domain.Options;
using DeyeSolar.Infrastructure.DeyeCloud;
using Microsoft.Extensions.Logging;

namespace DeyeSolar.Infrastructure.Tests;

public class DeyeCloudClientTests
{
    [Theory]
    [InlineData("https://deye-test.example/Api%20Zone/V1/device/latest", true)]
    [InlineData("HTTPS://DEYE-TEST.EXAMPLE:443/Api%20Zone/V1/device/historyRaw", true)]
    [InlineData("https://deye-test.example/Api%20Zone/v1/device/latest", false)]
    [InlineData("https://deye-test.example/api%20Zone/V1/device/latest", false)]
    [InlineData("https://deye-test.example/Api%20Zone/V10/device/latest", false)]
    [InlineData("https://deye-test.example/Api%20Zone/V1foreign/device/latest", false)]
    [InlineData("https://deye-test.example/Api%20Zone/V1/../foreign/device/latest", false)]
    [InlineData("https://deye-test.example/Api%20Zone%2FV1/device/latest", false)]
    [InlineData("https://deye-test.example.foreign.invalid/Api%20Zone/V1/device/latest", false)]
    [InlineData("http://deye-test.example/Api%20Zone/V1/device/latest", false)]
    [InlineData("https://deye-test.example:444/Api%20Zone/V1/device/latest", false)]
    [InlineData("https://foreign@deye-test.example/Api%20Zone/V1/device/latest", false)]
    public async Task CachedAuthenticationHonorsOriginAndCaseSensitiveBasePath(string target, bool accepted)
    {
        var options = new DeyeCloudOptions { BaseUrl = "HTTPS://DEYE-TEST.EXAMPLE:443/Api%20Zone/V1", DeviceSn = "test-device" };
        var handler = new QueueHttpMessageHandler(
            _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"success":true,"accessToken":"test-token","expiresIn":3600}""")),
            _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, LatestPayload("1700000000", "4100", "W"))));
        var client = new DeyeCloudClient(new HttpClient(handler), new TestOptionsMonitor<DeyeCloudOptions>(options),
            new CapturingLogger<DeyeCloudClient>());
        await client.ReadCurrentDataAsync(default);
        var tokenFor = typeof(DeyeCloudClient).GetMethod("AccessTokenFor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        if (accepted) Assert.Equal("test-token", tokenFor.Invoke(client, [target]));
        else
        {
            var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => tokenFor.Invoke(client, [target]));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }
    }

    [Theory]
    [InlineData("BaseUrl")]
    [InlineData("AppId")]
    [InlineData("AppSecret")]
    [InlineData("Email")]
    [InlineData("Password")]
    public async Task ChangedAccountSettingsNeverReuseCachedAuthentication(string changedProperty)
    {
        var options = new DeyeCloudOptions
        {
            BaseUrl = "https://original.example", AppId = "first-id", AppSecret = "first-secret",
            Email = "first@example.invalid", Password = "first-password", DeviceSn = "test-device"
        };
        var authenticated = new List<string>();
        var tokenRequests = new List<string>();
        Task<HttpResponseMessage> Token(HttpRequestMessage request, string token)
        {
            Assert.Null(request.Headers.Authorization);
            tokenRequests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                $$"""{"success":true,"accessToken":"{{token}}","expiresIn":3600}"""));
        }
        Task<HttpResponseMessage> Latest(HttpRequestMessage request)
        {
            authenticated.Add(request.Headers.Authorization!.Parameter!);
            Assert.Equal(options.BaseUrl + "/device/latest", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, LatestPayload("1700000000", "4100", "W")));
        }
        var handler = new QueueHttpMessageHandler(request => Token(request, "first-token"), Latest, Latest,
            request => Token(request, "second-token"), Latest);
        var client = new DeyeCloudClient(new HttpClient(handler), new TestOptionsMonitor<DeyeCloudOptions>(options),
            new CapturingLogger<DeyeCloudClient>());

        await client.ReadCurrentDataAsync(default);
        await client.ReadCurrentDataAsync(default);
        typeof(DeyeCloudOptions).GetProperty(changedProperty)!.SetValue(options,
            changedProperty == "BaseUrl" ? "https://replacement.example" : "replacement-value");
        await client.ReadCurrentDataAsync(default);

        Assert.Equal(new[] { "first-token", "first-token", "second-token" }, authenticated);
        Assert.Equal(2, tokenRequests.Count);
        Assert.StartsWith(options.BaseUrl + "/account/token?", tokenRequests[1]);
    }

    [Fact]
    public async Task AccountChangedDuringAuthenticationCannotPublishOrUseItsToken()
    {
        var options = new DeyeCloudOptions
        { BaseUrl = "https://deye.example", AppId = "id", AppSecret = "secret", Email = "first@example.invalid", Password = "password", DeviceSn = "test-device" };
        var firstResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var handler = new QueueHttpMessageHandler(
            _ => { requests++; started.SetResult(true); return firstResponse.Task; },
            _ => { requests++; return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"success":true,"accessToken":"new-token","expiresIn":3600}""")); },
            request =>
            {
                requests++;
                Assert.Equal("new-token", request.Headers.Authorization!.Parameter);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, LatestPayload("1700000000", "4100", "W")));
            });
        var client = new DeyeCloudClient(new HttpClient(handler), new TestOptionsMonitor<DeyeCloudOptions>(options),
            new CapturingLogger<DeyeCloudClient>());
        var first = client.ReadCurrentDataAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        options.Email = "replacement@example.invalid";
        firstResponse.SetResult(JsonResponse(HttpStatusCode.OK, """{"success":true,"accessToken":"obsolete-token","expiresIn":3600}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.Equal(1, requests);
        await client.ReadCurrentDataAsync(default);
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ReadCurrentDataAsync_LogsRetryableStatusAsWarning(HttpStatusCode statusCode)
    {
        var handler = new QueueHttpMessageHandler(
            _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, """
                {
                  "success": true,
                  "accessToken": "test-token",
                  "expiresIn": 3600
                }
                """)),
            _ => Task.FromResult(JsonResponse(statusCode, """
                {
                  "success": false,
                  "code": "3201001",
                  "msg": "Service Currently Unavailable"
                }
                """)));
        var logger = new CapturingLogger<DeyeCloudClient>();
        var client = CreateClient(handler, logger);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ReadCurrentDataAsync(CancellationToken.None));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Contains(logger.Levels, level => level == LogLevel.Warning);
        Assert.DoesNotContain(logger.Levels, level => level == LogLevel.Error);
    }

    [Fact]
    public async Task ReadCurrentDataAsync_LogsPermanentStatusAsError()
    {
        var handler = new QueueHttpMessageHandler(
            _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, """
                {
                  "success": true,
                  "accessToken": "test-token",
                  "expiresIn": 3600
                }
                """)),
            _ => Task.FromResult(JsonResponse(HttpStatusCode.Unauthorized, "{}")));
        var logger = new CapturingLogger<DeyeCloudClient>();
        var client = CreateClient(handler, logger);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ReadCurrentDataAsync(CancellationToken.None));

        Assert.Contains(logger.Levels, level => level == LogLevel.Error);
    }

    [Theory]
    [InlineData("1700000000", "4100", "W")]
    [InlineData("\"1700000000\"", "\"4100.9\"", "W")]
    [InlineData("1700000000", "0", "W")]
    [InlineData("1700000000", "4100", null)]
    public async Task ReadCurrentDataAsync_PreservesMeasuredTimeSeparatelyFromPollTime(
        string collectionTime, string value, string? unit)
    {
        var result = await ReadLatestAsync(LatestPayload(collectionTime, value, unit));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), result.SolarObservedAt);
        Assert.True(result.Timestamp > result.SolarObservedAt);
        Assert.Equal(value == "0" ? 0 : 4100, result.SolarProduction);
        Assert.Equal("test-device", result.SolarDeviceSn);
    }

    [Theory]
    [InlineData(-2742)]
    [InlineData(2742)]
    public async Task ReadCurrentDataAsync_KeepsSolarGenerationDistinctFromBatteryChargingAndDischarging(int batteryWatts)
    {
        var payload = LatestPayload("1700000000", "4100", "W").Replace(
            "\"dataList\": [", $$"""
            "dataList": [
              { "key": "BatteryPower", "value": {{batteryWatts}}, "unit": "W" },
            """);

        var result = await ReadLatestAsync(payload);

        Assert.Equal(4100, result.SolarProduction);
        Assert.Equal(batteryWatts, result.BatteryPower);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), result.SolarObservedAt);
        Assert.Equal("test-device", result.SolarDeviceSn);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"invalid\"")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("4102444800")]
    [InlineData("1700000000000")]
    [InlineData("9223372036854775807")]
    public async Task ReadCurrentDataAsync_RejectsInvalidFutureAndMillisecondMeasurementTimes(string collectionTime)
    {
        var result = await ReadLatestAsync(LatestPayload(collectionTime, "4100", "W"));

        Assert.Null(result.SolarObservedAt);
        Assert.Equal(4100, result.SolarProduction);
        Assert.Null(result.SolarDeviceSn);
    }

    [Fact]
    public async Task ReadCurrentDataAsync_DoesNotSubstitutePollingTimeWhenCollectionTimeIsMissing()
    {
        var payload = LatestPayload("1700000000", "4100", "W")
            .Replace("\"collectionTime\": 1700000000,", "");
        var result = await ReadLatestAsync(payload);

        Assert.Null(result.SolarObservedAt);
    }

    [Theory]
    [InlineData("null", "W")]
    [InlineData("\"\"", "W")]
    [InlineData("\"NaN\"", "W")]
    [InlineData("\"Infinity\"", "W")]
    [InlineData("-1", "W")]
    [InlineData("2147483648", "W")]
    [InlineData("4.1", "kW")]
    [InlineData("4100", "Wh")]
    public async Task ReadCurrentDataAsync_InvalidPowerCannotBecomeAnAlignedReading(string value, string unit)
    {
        var result = await ReadLatestAsync(LatestPayload("1700000000", value, unit));

        Assert.Null(result.SolarObservedAt);
        Assert.Null(result.SolarDeviceSn);
    }

    [Fact]
    public async Task ReadCurrentDataAsync_MissingSolarPowerCannotBecomeAnAlignedZero()
    {
        var result = await ReadLatestAsync(
            LatestPayload("1700000000", "4100", "W").Replace("TotalSolarPower", "TotalGridPower"));

        Assert.Null(result.SolarObservedAt);
    }

    [Fact]
    public async Task ReadCurrentDataAsync_DoesNotMixAnotherDeviceIntoSelectedDevice()
    {
        var payload = LatestPayload("1700000000", "4100", "W").Replace(
            "\"deviceDataList\": [", """
            "deviceDataList": [
              { "deviceSn": "another-device", "collectionTime": 1700000300,
                "dataList": [{ "key": "TotalSolarPower", "value": "9999", "unit": "W" }] },
            """);
        var result = await ReadLatestAsync(payload);

        Assert.Equal(4100, result.SolarProduction);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), result.SolarObservedAt);
        Assert.Equal("test-device", result.SolarDeviceSn);
    }

    [Fact]
    public async Task ReadCurrentDataAsync_RejectsResponseForOtherDevice()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadLatestAsync(
            LatestPayload("1700000000", "4100", "W").Replace("test-device", "another-device")));
    }

    private static string LatestPayload(string collectionTime, string power, string? unit)
        => $$"""
        {
          "success": true,
          "deviceDataList": [
            {
              "deviceSn": "test-device",
              "collectionTime": {{collectionTime}},
              "dataList": [
                { "key": "TotalSolarPower", "value": {{power}}{{(unit is null ? "" : $", \"unit\": \"{unit}\"")}} }
              ]
            }
          ]
        }
        """;

    [Theory]
    [InlineData("-2547", "W", -2547)]
    [InlineData("\"-2547.9\"", "W", -2547)]
    [InlineData("0", "W", 0)]
    [InlineData("3200", "w", 3200)]
    public async Task GridMeasurementHasItsOwnProvenanceWithoutPv(string power, string unit, int expectedWatts)
    {
        var result = await ReadLatestAsync(LatestPayload("\"1700000000\"", power, unit)
            .Replace("TotalSolarPower", "TotalGridPower"));

        Assert.Equal(expectedWatts, result.GridConsumption);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), result.GridObservedAt);
        Assert.Equal("test-device", result.GridDeviceSn);
        Assert.Null(result.SolarObservedAt);
        Assert.Null(result.SolarDeviceSn);
    }

    [Theory]
    [InlineData("-2547", null)]
    [InlineData("-2547", "kW")]
    [InlineData("null", "W")]
    [InlineData("\"NaN\"", "W")]
    [InlineData("\"Infinity\"", "W")]
    [InlineData("2147483648", "W")]
    [InlineData("-2147483649", "W")]
    [InlineData("\"1,500\"", "W")]
    public async Task InvalidOrUnprovenGridDoesNotBecomeAnAccountingObservation(string power, string? unit)
    {
        var result = await ReadLatestAsync(LatestPayload("1700000000", power, unit)
            .Replace("TotalSolarPower", "TotalGridPower"));
        Assert.Null(result.GridObservedAt);
        Assert.Null(result.GridDeviceSn);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("4102444800")]
    [InlineData("1700000000000")]
    public async Task GridRejectsInvalidMeasurementTimeEvenWithValidWatts(string timestamp)
    {
        var result = await ReadLatestAsync(LatestPayload(timestamp, "-2500", "W")
            .Replace("TotalSolarPower", "TotalGridPower"));
        Assert.Equal(-2500, result.GridConsumption);
        Assert.Null(result.GridObservedAt);
        Assert.Null(result.GridDeviceSn);
    }

    [Fact]
    public async Task ValidPvDoesNotCertifyMissingGridData()
    {
        var result = await ReadLatestAsync(LatestPayload("1700000000", "4000", "W"));
        Assert.NotNull(result.SolarObservedAt);
        Assert.Null(result.GridObservedAt);
        Assert.Null(result.GridDeviceSn);
    }

    [Fact]
    public async Task DuplicateGridFieldsCannotReuseAnotherFieldsUnitAsProvenance()
    {
        var payload = LatestPayload("1700000000", "-2500", null).Replace("TotalSolarPower", "TotalGridPower")
            .Replace("\"dataList\": [", "\"dataList\": [{\"key\":\"TotalGridPower\",\"value\":-1000,\"unit\":\"W\"},");
        var result = await ReadLatestAsync(payload);
        Assert.Null(result.GridObservedAt);
        Assert.Null(result.GridDeviceSn);
    }

    private static Task<DeyeSolar.Domain.Models.InverterData> ReadLatestAsync(string payload)
    {
        var handler = new QueueHttpMessageHandler(
            _ => Task.FromResult(JsonResponse(HttpStatusCode.OK,
                """{"success":true,"accessToken":"test-token","expiresIn":3600}""")),
            _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, payload)));
        return CreateClient(handler, new CapturingLogger<DeyeCloudClient>())
            .ReadCurrentDataAsync(CancellationToken.None);
    }

    private static DeyeCloudClient CreateClient(
        HttpMessageHandler handler,
        ILogger<DeyeCloudClient> logger)
    {
        var options = new TestOptionsMonitor<DeyeCloudOptions>(new DeyeCloudOptions
        {
            BaseUrl = "https://deye-test.example/v1.0",
            AppId = "test-app",
            AppSecret = "test-secret",
            Email = "test@example.com",
            Password = "test-password",
            DeviceSn = "test-device"
        });

        return new DeyeCloudClient(new HttpClient(handler), options, logger);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<LogLevel> Levels { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Levels.Add(logLevel);
}

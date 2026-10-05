using System.Net;
using System.Text;
using System.Text.Json;
using SolarManagement.Providers.DeyeCloud;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeyeSolar.Infrastructure.Tests;

public class DeyeExportHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("HTTPS://DEYE-TEST.EXAMPLE:443/v1.0")]
    [InlineData("https://Deye-Test.Example/Api%20Zone/V1")]
    [InlineData("https://deye-test.example:443/%76%31.0")]
    public async Task NormalizedHistoryUriPreservesConfiguredOriginAndEscapedBasePath(string baseUrl)
    {
        var handler = new Handler(Payload(Row(0, "-1500")));
        var options = Options();
        options.BaseUrl = baseUrl;
        var client = Client(handler, options);
        Assert.Equal(-1500, Assert.Single(await client.ReadAsync("selected", Start, Start.AddHours(1), default)).GridPowerWatts);
        Assert.Equal("Bearer test-token", handler.Authorization);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ReadsOnlyGridWattsWithDeviceIdentityUnitsAndHalfOpenMeasuredTimes()
    {
        var payload = Payload(
            Row(-1, "-9000"), Row(0, "\"-2500\""), Row(300, "400"), Row(300, "400"),
            Row(600, "0"), Row(900, "null"), Row(1200, "-2", "kW"), Row(3600, "-8000"));
        var handler = new Handler(payload);
        var result = await Client(handler).ReadAsync("selected", Start, Start.AddHours(1), default);

        Assert.Equal(new[] { -2500, 400, 0 }, result.Select(sample => sample.GridPowerWatts));
        Assert.Equal(new[] { Start, Start.AddMinutes(5), Start.AddMinutes(10) }, result.Select(sample => sample.Timestamp));
        Assert.Equal("Bearer test-token", handler.Authorization);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("selected", request.RootElement.GetProperty("deviceSn").GetString());
        Assert.Equal(Start.ToUnixTimeSeconds(), request.RootElement.GetProperty("startTimestamp").GetInt64());
        Assert.Equal(Start.AddHours(1).ToUnixTimeSeconds(), request.RootElement.GetProperty("endTimestamp").GetInt64());
        Assert.Equal("TotalGridPower", Assert.Single(request.RootElement.GetProperty("measurePoints").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1441)]
    public async Task RejectsUnboundedWindowsBeforeAuthentication(int minutes)
    {
        var handler = new Handler(Payload());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Client(handler).ReadAsync("selected", Start, Start.AddMinutes(minutes), default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("neighbor")]
    public async Task RejectsUnconfiguredDeviceWithoutSendingCredentials(string device)
    {
        var handler = new Handler(Payload());
        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).ReadAsync(device, Start, Start.AddHours(1), default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ForeignDeviceAndConflictingDuplicatesAreUnavailableRatherThanZero()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(new Handler(Payload(Row(0, "1")).Replace("selected", "neighbor")))
            .ReadAsync("selected", Start, Start.AddHours(1), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(new Handler(Payload(Row(0, "1"), Row(0, "2"))))
            .ReadAsync("selected", Start, Start.AddHours(1), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(new Handler("""{"success":false,"msg":"private detail"}"""))
            .ReadAsync("selected", Start, Start.AddHours(1), default));
    }

    [Fact]
    public async Task CancellationAndDeviceReplacementDoNotReturnOldDeviceHistory()
    {
        var handler = new Handler(Payload(Row(0, "1")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).ReadAsync("selected", Start, Start.AddHours(1), cancellation.Token));
        Assert.Equal(0, handler.Calls);

        var options = Options();
        handler.BeforeHistory = () => options.DeviceSn = "replacement";
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler, options).ReadAsync("selected", Start, Start.AddHours(1), default));
    }

    [Fact]
    public async Task PollingAndHistoryUseRequestAuthenticationWithoutMutatingSharedHeaders()
    {
        var handler = new Handler(Payload(Row(0, "-1500")));
        using var http = new HttpClient(handler);
        var client = new DeyeCloudClient(http, new TestOptionsMonitor<DeyeCloudOptions>(Options()), NullLogger<DeyeCloudClient>.Instance);
        await client.ReadAsync("selected", Start, Start.AddHours(1), default);

        await Task.WhenAll(client.ReadCurrentDataAsync(default), client.ReadAsync("selected", Start, Start.AddHours(1), default));

        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Equal("Bearer test-token", handler.Authorization);
    }

    [Fact]
    public async Task OversizedTimelineIsNotSilentlyTruncated()
    {
        var payload = Payload(Enumerable.Repeat(Row(0, "1"), 301).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(new Handler(payload)).ReadAsync("selected", Start, Start.AddHours(1), default));
    }

    private static string Row(int seconds, string value, string unit = "W") => $$"""
        {"time":"{{Start.AddSeconds(seconds).ToUnixTimeSeconds()}}","itemList":[{"key":"TotalGridPower","value":{{value}},"unit":"{{unit}}"}]}
        """;
    private static string Payload(params string[] rows) => "{\"success\":true,\"deviceSn\":\"selected\",\"dataList\":[" + string.Join(',', rows) + "]}";
    private static DeyeCloudOptions Options() => new() { BaseUrl = "https://deye-test.example/v1.0", DeviceSn = "selected" };
    private static DeyeCloudClient Client(Handler handler, DeyeCloudOptions? options = null) => new(new HttpClient(handler),
        new TestOptionsMonitor<DeyeCloudOptions>(options ?? Options()), NullLogger<DeyeCloudClient>.Instance);

    private sealed class Handler(string payload) : HttpMessageHandler
    {
        public int Calls;
        public string? Authorization;
        public string? Body;
        public Action? BeforeHistory;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/account/token"))
                return Json("""{"success":true,"accessToken":"test-token","expiresIn":3600}""");
            if (request.RequestUri.AbsolutePath.EndsWith("/device/latest"))
            {
                Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());
                return Json("""{"success":true,"deviceDataList":[{"deviceSn":"selected","collectionTime":1700000000,"dataList":[{"key":"TotalGridPower","value":-1500,"unit":"W"}]}]}""");
            }
            Assert.EndsWith("/device/historyRaw", request.RequestUri.AbsolutePath);
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(ct);
            BeforeHistory?.Invoke();
            return Json(payload);
        }
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
}

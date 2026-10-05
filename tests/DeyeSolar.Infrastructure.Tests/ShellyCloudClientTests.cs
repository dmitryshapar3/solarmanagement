using System.Net;
using System.Text.Json;
using SolarManagement.Providers.ShellyCloud;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Infrastructure.Tests;

public class ShellyCloudClientTests
{
    [Fact]
    public async Task TurnOnAsync_PostsSwitchSetCommand()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new QueueHttpMessageHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync();
            return JsonResponse("{}");
        });
        var client = CreateClient(handler);

        await client.TurnOnAsync("abc123", CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("/v2/devices/api/set/switch", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Contains("auth_key=test-key", capturedRequest.RequestUri?.Query);

        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("abc123", body.RootElement.GetProperty("id").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("channel").GetInt32());
        Assert.True(body.RootElement.GetProperty("on").GetBoolean());
    }

    [Fact]
    public async Task GetStatusAsync_ParsesGen2SwitchStatus()
    {
        var handler = new QueueHttpMessageHandler(_ => Task.FromResult(JsonResponse("""
            [
              {
                "id": "abc123",
                "code": "SPSW-001PE16EU",
                "gen": "G2",
                "online": 1,
                "status": {
                  "switch:0": {
                    "output": true,
                    "apower": 42.4
                  }
                },
                "settings": {
                  "sys": {
                    "device": {
                      "name": "Plug S"
                    }
                  }
                }
              }
            ]
            """)));
        var client = CreateClient(handler);

        var status = await client.GetStatusAsync("abc123", CancellationToken.None);

        Assert.True(status.IsOn);
        Assert.Equal(42, status.CurrentPowerW);
    }

    [Fact]
    public async Task GetDevicesWithStatusAsync_ParsesAllStatusAndFiltersSwitchDevices()
    {
        var handler = new QueueHttpMessageHandler(_ => Task.FromResult(JsonResponse("""
            {
              "isok": true,
              "data": {
                "devices_status": {
                  "abc123": {
                    "_dev_info": {
                      "id": "abc123",
                      "gen": "G2",
                      "code": "SPSW-001PE16EU",
                      "online": true
                    },
                    "switch:0": {
                      "output": true,
                      "apower": 10.9
                    }
                  },
                  "thermostat": {
                    "_dev_info": {
                      "id": "thermostat",
                      "gen": "V1",
                      "code": "THERMOSTAT",
                      "online": true
                    }
                  }
                }
              }
            }
            """)));
        var client = CreateClient(handler);

        var devices = await client.GetDevicesWithStatusAsync(CancellationToken.None);

        var device = Assert.Single(devices);
        Assert.Equal("abc123", device.Id);
        Assert.True(device.Online);
        Assert.True(device.IsOn);
        Assert.Equal(11, device.CurrentPowerW);
    }

    [Fact]
    public async Task NormalizedInventoryKeepsChannelsIndependentAndDoesNotInventMissingState()
    {
        var client = CreateClient(new QueueHttpMessageHandler(_ => Task.FromResult(JsonResponse("""
            {"isok":true,"data":{"devices_status":{
              "shared":{"online":true,"switch:0":{"output":true,"apower":42},"switch:1":{"output":false,"apower":0}},
              "unknown":{"code":"SPSW","switch:0":{}}
            }}}
            """))));
        var devices = await client.GetNormalizedInventoryAsync(default);
        Assert.Equal(3, devices.Count);
        var first = Assert.Single(devices, device => device.RemoteId == "shared" && device.Channel == "0");
        var second = Assert.Single(devices, device => device.RemoteId == "shared" && device.Channel == "1");
        Assert.True(first.IsOn);
        Assert.False(second.IsOn);
        Assert.Equal(42, first.CurrentPowerWatts);
        Assert.Equal(0, second.CurrentPowerWatts);
        var unknown = Assert.Single(devices, device => device.RemoteId == "unknown");
        Assert.Null(unknown.IsOn);
        Assert.Null(unknown.Online);
        Assert.Null(unknown.CurrentPowerWatts);
    }

    [Fact]
    public async Task ChannelCommandTargetsOnlyRequestedRelayAndRejectsInvalidChannelBeforeHttp()
    {
        string? captured = null;
        var calls = 0;
        var client = CreateClient(new QueueHttpMessageHandler(async request =>
        {
            calls++;
            captured = await request.Content!.ReadAsStringAsync();
            return JsonResponse("{}");
        }));
        await client.SetChannelStateAsync("shared", 1, true, default);
        using var body = JsonDocument.Parse(captured!);
        Assert.Equal("shared", body.RootElement.GetProperty("id").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("channel").GetInt32());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetChannelStateAsync("neighbor", 200, true, default));
        Assert.Equal(1, calls);
    }

    private static ShellyCloudClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var options = new TestOptionsMonitor<ShellyOptions>(new ShellyOptions
        {
            ServerUri = "https://shelly-test.example",
            AuthKey = "test-key",
            RequestIntervalMilliseconds = 0
        });

        return new ShellyCloudClient(httpClient, options, NullLogger<ShellyCloudClient>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
}

internal sealed class QueueHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _responses;

    public QueueHttpMessageHandler(params Func<HttpRequestMessage, Task<HttpResponseMessage>>[] responses)
    {
        _responses = new Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>(responses);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_responses.Count == 0)
            throw new InvalidOperationException("No fake response queued.");

        return _responses.Dequeue()(request);
    }
}

internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T currentValue)
    {
        CurrentValue = currentValue;
    }

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

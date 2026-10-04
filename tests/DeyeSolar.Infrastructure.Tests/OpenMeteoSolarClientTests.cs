using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Options;
using DeyeSolar.Infrastructure.Solar;

namespace DeyeSolar.Infrastructure.Tests;

public sealed class OpenMeteoSolarClientTests
{
    private static readonly DateTimeOffset ObservationTime = DateTimeOffset.Parse("2026-09-18T11:00:00Z");
    private static readonly DateTimeOffset Now = ObservationTime.AddMinutes(25);

    [Fact]
    public async Task ReadAsync_UsesNativeDwdRequestsAndLatestCommonValidNonfutureTimestamp()
    {
        var handler = new RoutingHandler((uri, _) =>
        {
            if (IsWeather(uri)) return Json(Weather([0, 60], [19.4, 20], [1.2, 2]));
            return Json(Satellite([-20, -10, 0, 10, 20, 30], IsRoof1(uri)
                ? [100, 200, 300, null, 500, 700] : [50, 60, 70, 80, null, 99]));
        });

        var result = await Client(handler).ReadAsync(new() { Roof1Kwp = 4.05, Roof2Kwp = 4.05,
            Roof1Tilt = 23, Roof2Tilt = 23 }, Now, CancellationToken.None);

        Assert.Equal(ObservationTime, result.Timestamp);
        Assert.Equal(300, result.Roof1Gti);
        Assert.Equal(70, result.Roof2Gti);
        Assert.Equal(19.4, result.AirTemperatureC);
        Assert.Equal(1.2, result.WindSpeedMs);
        Assert.Equal(ObservationTime, result.WeatherTimestamp);
        Assert.True(result.RecentVariabilityFraction > 0.4);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains(handler.Requests, uri => uri.Query.Contains("azimuth=50"));
        Assert.Contains(handler.Requests, uri => uri.Query.Contains("azimuth=-130"));
        foreach (var uri in handler.Requests.Where(uri => !IsWeather(uri)))
        {
            Assert.Equal("satellite-api.open-meteo.com", uri.Host);
            Assert.Equal("/v1/archive", uri.AbsolutePath);
            Assert.Contains("latitude=50.095278", uri.Query);
            Assert.Contains("longitude=20.070278", uri.Query);
            Assert.Contains("tilt=23", uri.Query);
            Assert.Contains("models=dwd_sis_europe_africa_v4", uri.Query);
            Assert.Contains("temporal_resolution=native", uri.Query);
            Assert.Contains("hourly=global_tilted_irradiance_instant", uri.Query);
            Assert.Contains("timeformat=unixtime", uri.Query);
            Assert.Contains("timezone=UTC", uri.Query);
        }
        Assert.Contains(handler.Requests, uri => IsWeather(uri) && uri.Query.Contains("wind_speed_unit=ms"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(2001d)]
    public async Task ReadAsync_SkipsInvalidRadiationInsteadOfCoercingItToZero(double? invalid)
    {
        var handler = new RoutingHandler((uri, _) => Json(IsWeather(uri) ? ValidWeather()
            : Satellite([-10, 0, 10], [100, 200, invalid])));

        var result = await Client(handler).ReadAsync(new(), Now, CancellationToken.None);

        Assert.Equal(ObservationTime, result.Timestamp);
        Assert.Equal(200, result.Roof1Gti);
        Assert.Equal(0.4, result.RecentVariabilityFraction);
    }

    [Fact]
    public async Task ReadAsync_TruncatedRadiationArrayUsesOnlyItsActualValues()
    {
        var handler = new RoutingHandler((uri, _) => Json(IsWeather(uri) ? ValidWeather()
            : Satellite([-10, 0, 10], [100, 200])));

        var result = await Client(handler).ReadAsync(new(), Now, CancellationToken.None);

        Assert.Equal(ObservationTime, result.Timestamp);
        Assert.Equal(200, result.Roof1Gti);
    }

    [Fact]
    public async Task ReadAsync_NighttimeZeroIsValid()
    {
        var handler = new RoutingHandler((uri, _) => Json(IsWeather(uri) ? ValidWeather()
            : Satellite([-20, -10, 0], [0, 0, 0])));

        var result = await Client(handler).ReadAsync(new(), Now, CancellationToken.None);

        Assert.Equal(0, result.Roof1Gti);
        Assert.Equal(0, result.Roof2Gti);
        Assert.Equal(0, result.RecentVariabilityFraction);
    }

    [Fact]
    public async Task ReadAsync_AllFutureRadiationIsUnavailable()
    {
        var handler = new RoutingHandler((_, _) => Json(Satellite([30, 40, 50], [100, 200, 300])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Now, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_DifferentRoofTimeArraysIntersectByTimestamp()
    {
        var handler = new RoutingHandler((uri, _) => Json(IsWeather(uri) ? ValidWeather()
            : IsRoof1(uri) ? Satellite([-10, 0, 10], [100, 200, 300])
            : Satellite([-20, -10, 0], [40, 50, 60])));

        var result = await Client(handler).ReadAsync(new(), Now, CancellationToken.None);

        Assert.Equal(ObservationTime, result.Timestamp);
        Assert.Equal(200, result.Roof1Gti);
        Assert.Equal(60, result.Roof2Gti);
    }

    [Fact]
    public async Task ReadAsync_NoMatchingRoofTimestampsIsUnavailable()
    {
        var handler = new RoutingHandler((uri, _) => Json(IsRoof1(uri)
            ? Satellite([-20, -10, 0], [100, 200, 300])
            : Satellite([-15, -5, 5], [100, 200, 300])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Now, CancellationToken.None));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"hourly_units\":{\"time\":\"unixtime\",\"global_tilted_irradiance_instant\":\"W/m²\"}}")]
    [InlineData("{\"hourly_units\":{\"time\":\"iso8601\",\"global_tilted_irradiance_instant\":\"W/m²\"}}")]
    [InlineData("not json")]
    public async Task ReadAsync_MissingOrMalformedSatelliteResponseFails(string json)
    {
        var handler = new RoutingHandler((_, _) => Json(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Now, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count); // Bad data is not retried.
    }

    [Theory]
    [InlineData("kW/m²")]
    [InlineData("W/m2")]
    public async Task ReadAsync_UnexpectedSatelliteUnitsFail(string unit)
    {
        var handler = new RoutingHandler((_, _) => Json(Satellite([-10, 0], [100, 200], unit)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Now, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_HourlySatelliteDataIsRejected()
    {
        var handler = new RoutingHandler((_, _) => Json(Satellite([-120, -60, 0], [100, 200, 300])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Now, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_OldObservationsKeepTheirOriginalTimestampForCallerStalenessRules()
    {
        var handler = new RoutingHandler((uri, _) => Json(IsWeather(uri) ? ValidWeather()
            : Satellite([-200, -190, -180], [100, 200, 300])));
        var result = await Client(handler).ReadAsync(new(), Now, CancellationToken.None);
        Assert.Equal(ObservationTime.AddHours(-3), result.Timestamp);
        Assert.Null(result.WeatherTimestamp);
    }

    [Fact]
    public async Task ReadAsync_WeatherUsesNearestNonfuturePointRelativeToSatellite()
    {
        var handler = new RoutingHandler((uri, _) => Json(IsWeather(uri)
            ? Weather([-120, -60, 10], [12, 15, 22], [1, 2, 3]) : ValidSatellite()));
        var result = await Client(handler).ReadAsync(new(), Now, CancellationToken.None);
        Assert.Equal(ObservationTime.AddHours(-1), result.WeatherTimestamp);
        Assert.Equal(15, result.AirTemperatureC);
        Assert.Equal(2, result.WindSpeedMs);
    }

    [Theory]
    [InlineData("°C", "km/h")]
    [InlineData("°F", "m/s")]
    public async Task ReadAsync_UnexpectedWeatherUnitsAreMissingWeather(string temperatureUnit, string windUnit)
    {
        var handler = new RoutingHandler((uri, _) => Json(IsWeather(uri)
            ? Weather([0], [19], [2], temperatureUnit, windUnit) : ValidSatellite()));
        var result = await Client(handler).ReadAsync(new(), Now, CancellationToken.None);
        Assert.Null(result.AirTemperatureC);
        Assert.Null(result.WindSpeedMs);
        Assert.Null(result.WeatherTimestamp);
        Assert.Equal(300, result.Roof1Gti);
    }

    [Theory]
    [InlineData(-91, 20d, 2d)]
    [InlineData(10, 20d, 2d)]
    [InlineData(0, null, 2d)]
    [InlineData(0, 20d, null)]
    [InlineData(0, 20d, -1d)]
    [InlineData(0, 100d, 2d)]
    public async Task ReadAsync_MissingInvalidOldOrFutureWeatherIsNullable(int minutes, double? temperature, double? wind)
    {
        var handler = new RoutingHandler((uri, _) => Json(IsWeather(uri)
            ? Weather([minutes], [temperature], [wind]) : ValidSatellite()));
        var result = await Client(handler).ReadAsync(new(), Now, CancellationToken.None);
        Assert.Null(result.AirTemperatureC);
        Assert.Null(result.WindSpeedMs);
        Assert.Null(result.WeatherTimestamp);
    }

    [Fact]
    public async Task ReadAsync_WeatherHttpFailureDoesNotDiscardSatelliteObservation()
    {
        var handler = new RoutingHandler((uri, _) => IsWeather(uri)
            ? new(HttpStatusCode.BadRequest) : Json(ValidSatellite()));
        var result = await Client(handler).ReadAsync(new(), Now, CancellationToken.None);
        Assert.Equal(ObservationTime, result.Timestamp);
        Assert.Null(result.AirTemperatureC);
        Assert.Null(result.WindSpeedMs);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ReadAsync_RetriesTransientResponseUpToSuccess(HttpStatusCode failure)
    {
        var handler = new RoutingHandler((uri, attempt) => IsRoof1(uri) && attempt < 3
            ? WithRetryAfter(failure, TimeSpan.Zero)
            : Json(IsWeather(uri) ? ValidWeather() : ValidSatellite()));
        var result = await Client(handler).ReadAsync(new(), Now, CancellationToken.None);
        Assert.Equal(ObservationTime, result.Timestamp);
        Assert.Equal(3, handler.Requests.Count(IsRoof1));
    }

    [Fact]
    public async Task ReadAsync_DoesNotRetryBeforeLongRetryAfterExpires()
    {
        var handler = new RoutingHandler((uri, _) => IsRoof1(uri)
            ? WithRetryAfter(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(10))
            : Json(ValidSatellite()));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).ReadAsync(new(), Now, CancellationToken.None));
        Assert.Single(handler.Requests, IsRoof1);
    }

    [Fact]
    public async Task ReadAsync_LongRetryAfterSuppressesLaterRefreshesUntilTheFullServerDeadline()
    {
        var handler = new RoutingHandler((_, _) => WithRetryAfter(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(30)));
        var client = Client(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadAsync(new(), Now, CancellationToken.None));
        var firstRefreshRequests = handler.Requests.Count;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadAsync(new(), Now.AddMinutes(10), CancellationToken.None));
        Assert.Equal(firstRefreshRequests, handler.Requests.Count);
    }

    [Fact]
    public async Task ReadAsync_PersistentTransientFailureStopsAfterThreeAttempts()
    {
        var handler = new RoutingHandler((uri, _) => IsRoof1(uri)
            ? WithRetryAfter(HttpStatusCode.ServiceUnavailable, TimeSpan.Zero) : Json(ValidSatellite()));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).ReadAsync(new(), Now, CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(3, handler.Requests.Count(IsRoof1));
    }

    [Fact]
    public async Task ReadAsync_NetworkErrorsAreBoundedAndDoNotExposeApiKey()
    {
        const string apiKey = "test/secret?value";
        var handler = new RoutingHandler((uri, _) => IsRoof1(uri)
            ? throw new HttpRequestException("Network error at " + uri)
            : Json(ValidSatellite()));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).ReadAsync(
            new() { ApiKey = apiKey }, Now, CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count(IsRoof1));
        Assert.DoesNotContain(apiKey, error.ToString());
        Assert.DoesNotContain("apikey", error.ToString());
    }

    [Fact]
    public async Task ReadAsync_TimeoutErrorsAreRetried()
    {
        var handler = new RoutingHandler((uri, attempt) => IsRoof1(uri) && attempt == 1
            ? throw new TaskCanceledException("Request timed out")
            : Json(IsWeather(uri) ? ValidWeather() : ValidSatellite()));
        var result = await Client(handler).ReadAsync(new(), Now, CancellationToken.None);
        Assert.Equal(ObservationTime, result.Timestamp);
        Assert.Equal(2, handler.Requests.Count(IsRoof1));
    }

    [Fact]
    public async Task ReadAsync_CancellationPropagatesWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RoutingHandler((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).ReadAsync(new(), Now, cancellation.Token));
        Assert.InRange(handler.Requests.Count, 1, 2);
    }

    [Fact]
    public async Task ReadAsync_CancellationDuringWeatherPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RoutingHandler((uri, _) =>
        {
            if (!IsWeather(uri)) return Json(ValidSatellite());
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).ReadAsync(new(), Now, cancellation.Token));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ReadAsync_ApiKeyUsesOnlyCustomerHostsAndEscapedQuery()
    {
        const string apiKey = "value/with?reserved&characters";
        var handler = new RoutingHandler((uri, _) => Json(IsWeather(uri) ? ValidWeather() : ValidSatellite()));
        await Client(handler).ReadAsync(new() { ApiKey = "  " + apiKey + "\n" }, Now, CancellationToken.None);
        Assert.All(handler.Requests, uri =>
        {
            Assert.Equal("https", uri.Scheme);
            Assert.Equal(IsWeather(uri) ? "customer-api.open-meteo.com" : "customer-satellite-api.open-meteo.com", uri.Host);
            Assert.Contains("apikey=" + Uri.EscapeDataString(apiKey), uri.Query);
            Assert.DoesNotContain("&characters", uri.Query);
        });
    }

    private static OpenMeteoSolarClient Client(HttpMessageHandler handler) => new(new HttpClient(handler));
    private static bool IsWeather(Uri uri) => uri.AbsolutePath == "/v1/forecast";
    private static bool IsRoof1(Uri uri) => uri.Query.Contains("azimuth=50");
    private static string ValidSatellite() => Satellite([-20, -10, 0], [100, 200, 300]);
    private static string ValidWeather() => Weather([0], [19.4], [1.2]);

    private static string Satellite(int[] minutes, double?[] values, string unit = "W/m²") =>
        JsonSerializer.Serialize(new
        {
            hourly_units = new { time = "unixtime", global_tilted_irradiance_instant = unit },
            hourly = new
            {
                time = minutes.Select(minute => ObservationTime.AddMinutes(minute).ToUnixTimeSeconds()),
                global_tilted_irradiance_instant = values
            }
        });

    private static string Weather(int[] minutes, double?[] temperatures, double?[] winds,
        string temperatureUnit = "°C", string windUnit = "m/s") => JsonSerializer.Serialize(new
        {
            hourly_units = new { time = "unixtime", temperature_2m = temperatureUnit, wind_speed_10m = windUnit },
            hourly = new
            {
                time = minutes.Select(minute => ObservationTime.AddMinutes(minute).ToUnixTimeSeconds()),
                temperature_2m = temperatures,
                wind_speed_10m = winds
            }
        });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage WithRetryAfter(HttpStatusCode status, TimeSpan delay)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }

    private sealed class RoutingHandler(Func<Uri, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _attempts = new();
        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            var attempt = _attempts.AddOrUpdate(uri.ToString(), 1, (_, previous) => previous + 1);
            return Task.FromResult(respond(uri, attempt));
        }
    }
}

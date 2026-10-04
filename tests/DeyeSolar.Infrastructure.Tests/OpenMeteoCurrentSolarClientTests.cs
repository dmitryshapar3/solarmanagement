using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Infrastructure.Solar;

namespace DeyeSolar.Infrastructure.Tests;

public sealed class OpenMeteoCurrentSolarClientTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 18, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = BaseTime.AddMinutes(7.5);
    private static readonly int[] Times = [-60, -45, -30, -15, 0, 15, 30, 45, 60];
    private static readonly double?[] Irradiance = [200, 250, 300, 350, 400, 700, 800, 850, 900];

    [Fact]
    public async Task UsesInstantModelRadiationForBothTiltsAndInterpolatesToNow()
    {
        var handler = new Handler((uri, _) => Json(Weather(gti: IsRoof1(uri)
            ? Irradiance : Irradiance.Select(value => value / 2).ToArray())));
        var result = await Client(handler).ReadAsync(new(), Now, default);

        Assert.Equal(SolarRadiationKind.WeatherModel, result.Kind);
        Assert.Equal(Now, result.Timestamp);
        Assert.Equal(Now, result.RetrievedAt);
        Assert.Equal(BaseTime, result.ModelPeriodStart);
        Assert.Equal(BaseTime.AddMinutes(15), result.ModelPeriodEnd);
        Assert.Equal(550, result.Roof1Gti);
        Assert.Equal(275, result.Roof2Gti);
        Assert.Equal(20.5, result.AirTemperatureC);
        Assert.Equal(0.9, result.WindSpeedMs!.Value, 8);
        Assert.Equal(45, result.CloudCoverPercent);
        Assert.Contains(result.Forecast!, point => point.Timestamp >= Now.AddMinutes(30));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(handler.Requests, uri => uri.Query.Contains("azimuth=50"));
        Assert.Contains(handler.Requests, uri => uri.Query.Contains("azimuth=-130"));
        Assert.All(handler.Requests, uri =>
        {
            Assert.Equal("api.open-meteo.com", uri.Host);
            Assert.Equal("/v1/forecast", uri.AbsolutePath);
            Assert.Contains("models=best_match", uri.Query);
            Assert.Contains("minutely_15=global_tilted_irradiance_instant", uri.Query);
            Assert.Contains("temperature_2m", uri.Query);
            Assert.Contains("wind_speed_10m", uri.Query);
            Assert.Contains("cloud_cover", uri.Query);
            Assert.Contains("wind_speed_unit=ms", uri.Query);
            Assert.Contains("timeformat=unixtime", uri.Query);
            Assert.Contains("timezone=UTC", uri.Query);
            Assert.Contains("tilt=25", uri.Query);
            Assert.Contains("past_minutely_15=5", uri.Query);
            Assert.Contains("forecast_minutely_15=5", uri.Query);
            Assert.DoesNotContain("temporal_resolution=native", uri.Query);
        });
    }

    [Fact]
    public async Task CloudCoverIsReportedWithoutScalingTheAlreadyCloudAdjustedGti()
    {
        var handler = new Handler((_, _) => Json(Weather(clouds: Enumerable.Repeat<double?>(100, Times.Length).ToArray())));
        var result = await Client(handler).ReadAsync(new(), Now, default);

        Assert.Equal(100, result.CloudCoverPercent);
        Assert.Equal(550, result.Roof1Gti);
        Assert.Equal(550, result.Roof2Gti);
    }

    [Fact]
    public async Task NighttimeModelZeroIsValid()
    {
        var handler = new Handler((_, _) => Json(Weather(gti: new double?[Times.Length].Select(_ => (double?)0).ToArray())));
        var result = await Client(handler).ReadAsync(new(), Now, default);

        Assert.Equal(0, result.Roof1Gti);
        Assert.Equal(0, result.Roof2Gti);
        Assert.Equal(0, result.RecentVariabilityFraction);
        Assert.Equal(SolarRadiationKind.WeatherModel, result.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(2001d)]
    public async Task InvalidValueOnOneRoofCannotBeFilledFromAnotherTimeOrTheOtherRoof(double? bad)
    {
        var invalid = Irradiance.ToArray();
        invalid[4] = bad;
        var handler = new Handler((uri, _) => Json(Weather(gti: IsRoof1(uri) ? invalid : Irradiance)));

        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Now, default));
    }

    [Fact]
    public async Task MissingFutureIntervalDoesNotAllowInterpolationAcrossThirtyMinuteGap()
    {
        var invalid = Irradiance.ToArray();
        invalid[6] = null;
        var handler = new Handler((_, _) => Json(Weather(gti: invalid)));

        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Now, default));
    }

    [Fact]
    public async Task MissingOldValueDoesNotDiscardValidCurrentAndFutureCoverage()
    {
        var invalid = Irradiance.ToArray();
        invalid[1] = null;
        var handler = new Handler((uri, _) => Json(Weather(gti: IsRoof1(uri) ? invalid : Irradiance)));
        var result = await Client(handler).ReadAsync(new(), Now, default);

        Assert.Equal(550, result.Roof1Gti);
        Assert.DoesNotContain(result.Forecast!, sample => sample.Timestamp == BaseTime.AddMinutes(-45));
    }

    [Fact]
    public async Task FutureOnlyResponseDoesNotPretendToObserveNow()
    {
        var handler = new Handler((_, _) => Json(Weather(times: [15, 30, 45, 60], gti: [400, 500, 600, 700])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Now, default));
    }

    [Fact]
    public async Task TruncatedRadiationDoesNotCreateFutureZerosOrPermitExtrapolation()
    {
        var handler = new Handler((_, _) => Json(Weather(gti: [200, 250, 300, 350, 400])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Now, default));
    }

    [Fact]
    public async Task HourlyTimelineCannotMasqueradeAsFifteenMinuteOutput()
    {
        var handler = new Handler((_, _) => Json(Weather(times: [-60, 0, 60], gti: [200, 400, 600])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Now, default));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("invalid json")]
    public async Task MissingOrMalformedResponseIsUnavailable(string response)
    {
        var handler = new Handler((_, _) => Json(response));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Now, default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UnexpectedRadiationUnitIsRejected()
    {
        var handler = new Handler((_, _) => Json(Weather(gtiUnit: "kW/m²")));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Now, default));
    }

    [Fact]
    public async Task MissingOrUnexpectedOptionalWeatherDoesNotBecomeInventedWeather()
    {
        var handler = new Handler((_, _) => Json(Weather(temperatureUnit: "°F", windUnit: "km/h",
            clouds: new double?[Times.Length])));
        var result = await Client(handler).ReadAsync(new(), Now, default);

        Assert.Equal(550, result.Roof1Gti);
        Assert.Null(result.AirTemperatureC);
        Assert.Null(result.WindSpeedMs);
        Assert.Null(result.CloudCoverPercent);
    }

    [Fact]
    public async Task TransientFailuresHaveBoundedRetriesAndCanRecover()
    {
        var handler = new Handler((uri, attempt) => IsRoof1(uri) && attempt < 3
            ? Retry(HttpStatusCode.ServiceUnavailable, TimeSpan.Zero) : Json(Weather()));
        var result = await Client(handler).ReadAsync(new(), Now, default);

        Assert.Equal(550, result.Roof1Gti);
        Assert.Equal(3, handler.Requests.Count(IsRoof1));
    }

    [Fact]
    public async Task PaidForecastUsesCustomerHostWithTrimmedEscapedServerKey()
    {
        const string key = "server/key?value&other=1";
        var handler = new Handler((_, _) => Json(Weather()));
        await Client(handler).ReadAsync(new() { ApiKey = "  " + key + "\n" }, Now, default);

        Assert.All(handler.Requests, uri =>
        {
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("customer-api.open-meteo.com", uri.Host);
            Assert.Equal("/v1/forecast", uri.AbsolutePath);
            Assert.Contains("apikey=" + Uri.EscapeDataString(key), uri.Query);
            Assert.DoesNotContain("&other=1", uri.Query);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    public async Task MissingKeyKeepsEvaluationEndpointWithoutKeyParameter(string? key)
    {
        var handler = new Handler((_, _) => Json(Weather()));
        await Client(handler).ReadAsync(new() { ApiKey = key }, Now, default);
        Assert.All(handler.Requests, uri =>
        {
            Assert.Equal("api.open-meteo.com", uri.Host);
            Assert.DoesNotContain("apikey", uri.Query);
        });
    }

    [Fact]
    public async Task CallerCancellationDoesNotExposeUrlFromTransportException()
    {
        const string key = "server/key?secret";
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler((uri, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException("Canceled URL " + uri, cancellation.Token);
        });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(handler).ReadAsync(new() { ApiKey = key }, Now, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.DoesNotContain("apikey", error.ToString());
        Assert.DoesNotContain(key, error.ToString());
        Assert.Null(error.InnerException);
        Assert.InRange(handler.Requests.Count, 1, 2);
    }

    [Fact]
    public async Task LongRetryAfterBlocksLaterRefreshesWithoutLeakingCustomerApiKey()
    {
        const string key = "private/forecast?key";
        var handler = new Handler((_, _) => Retry(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(1)));
        var client = Client(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadAsync(new() { ApiKey = key }, Now, default));
        var sent = handler.Requests.Count;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadAsync(new() { ApiKey = key }, Now.AddMinutes(10), default));

        Assert.Equal(sent, handler.Requests.Count);
        Assert.DoesNotContain(key, error.ToString());
        Assert.DoesNotContain("apikey", error.ToString());
        Assert.All(handler.Requests, uri => Assert.Equal("customer-api.open-meteo.com", uri.Host));
    }

    [Fact]
    public async Task NetworkErrorsAreBoundedAndSanitized()
    {
        const string key = "private/forecast?key";
        var handler = new Handler((uri, _) => throw new HttpRequestException("Failed URL " + uri));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).ReadAsync(new() { ApiKey = key }, Now, default));

        Assert.Equal(6, handler.Requests.Count);
        Assert.DoesNotContain(key, error.ToString());
        Assert.DoesNotContain("apikey", error.ToString());
    }

    [Fact]
    public async Task CancellationStopsRequestsWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).ReadAsync(new(), Now, cancellation.Token));
        Assert.InRange(handler.Requests.Count, 1, 2);
    }

    private static OpenMeteoCurrentSolarClient Client(HttpMessageHandler handler) => new(new HttpClient(handler));
    private static bool IsRoof1(Uri uri) => uri.Query.Contains("azimuth=50");
    private static string Weather(int[]? times = null, double?[]? gti = null, double?[]? clouds = null,
        string gtiUnit = "W/m²", string temperatureUnit = "°C", string windUnit = "m/s")
    {
        times ??= Times;
        return JsonSerializer.Serialize(new
        {
            minutely_15_units = new { time = "unixtime", global_tilted_irradiance_instant = gtiUnit,
                temperature_2m = temperatureUnit, wind_speed_10m = windUnit, cloud_cover = "%" },
            minutely_15 = new
            {
                time = times.Select(minute => BaseTime.AddMinutes(minute).ToUnixTimeSeconds()),
                global_tilted_irradiance_instant = gti ?? Irradiance,
                temperature_2m = times.Select((_, index) => 16d + index),
                wind_speed_10m = times.Select((_, index) => 0.2 * index),
                cloud_cover = clouds ?? times.Select((_, index) => (double?)(10 * index)).ToArray()
            }
        });
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Retry(HttpStatusCode status, TimeSpan delay)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }

    private sealed class Handler(Func<Uri, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _attempts = new();
        public ConcurrentQueue<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            return Task.FromResult(respond(uri, _attempts.AddOrUpdate(uri.ToString(), 1, (_, count) => count + 1)));
        }
    }
}

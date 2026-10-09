using static DeyeSolar.Infrastructure.Tests.HttpResponses;
using System.Globalization;
using System.Net;
using System.Text.Json;
using DeyeSolar.Domain.Options;
using DeyeSolar.Infrastructure.Solar;

namespace DeyeSolar.Infrastructure.Tests;

public sealed class OpenMeteoSolarHistoryClientTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 18, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FullDayForecastKeepsUpcomingHoursAndUsesVerifiedAstronomyWithoutChangingCompletedHistory()
    {
        var now = Start.AddMinutes(30);
        var sunrise = Start.AddHours(-6); var sunset = Start.AddHours(7);
        var handler = new RoutedHttpHandler((uri, _) => Json(uri.Query.Contains("daily=")
            ? JsonSerializer.Serialize(new { daily_units = new { sunrise="unixtime", sunset="unixtime" },
                daily = new { sunrise=new long?[] {sunrise.ToUnixTimeSeconds(),sunrise.AddDays(1).ToUnixTimeSeconds()},
                    sunset=new long?[] {sunset.ToUnixTimeSeconds(),sunset.AddDays(1).ToUnixTimeSeconds()} } }) : Weather()));
        var clock = new ForecastClock(now);
        var client = new OpenMeteoSolarHistoryClient(new OpenMeteoJsonReader(new HttpClient(handler),clock),clock);
        var forecast=await client.ReadAsync(new() {TimeZoneId="UTC"},Start,Start.AddHours(3),new DateOnly(2026,9,18),default);
        Assert.Equal(3,forecast.Samples.Count);Assert.Contains(forecast.Samples,p=>p.Timestamp>now);
        Assert.Equal(sunrise,forecast.Sunrise);Assert.Equal(sunset,forecast.Sunset);Assert.Equal(sunrise.AddDays(1),forecast.NextSunrise);Assert.Equal(now,forecast.RetrievedAt);
        var history=await client.ReadAsync(new(),Start,Start.AddHours(3),default);
        Assert.Empty(history); // 11:00–12:00 is still unfinished at 11:30.
    }
    [Theory]
    [InlineData("iso8601", 1726635600L)]
    [InlineData("unixtime", 0L)]
    [InlineData("unixtime", 1726635600L)]
    public async Task UnsupportedPolarOrForeignDateAstronomyIsHiddenRatherThanInvented(string unit,long seconds)
    {
        var handler=new RoutedHttpHandler((uri,_)=>Json(uri.Query.Contains("daily=")
            ? JsonSerializer.Serialize(new {daily_units=new {sunrise=unit,sunset=unit},daily=new {sunrise=new long?[] {seconds,null},sunset=new long?[] {seconds,null}}}) : Weather()));
        var result=await Client(handler).ReadAsync(new(),Start,Start.AddHours(1),new DateOnly(2026,9,18),default);
        Assert.Null(result.Sunrise);Assert.Null(result.Sunset);Assert.Null(result.NextSunrise);Assert.NotEmpty(result.Samples);
    }
    private sealed class ForecastClock(DateTimeOffset now):TimeProvider {public override DateTimeOffset GetUtcNow()=>now;}

    [Fact]
    public async Task ForwardMonthStopsAtLastReturnedRadiationHourWithoutRequestingDatesBeyondTheProviderHorizon()
    {
        var today = new DateTimeOffset(Start.UtcDateTime.Date, TimeSpan.Zero);
        var handler = new RoutedHttpHandler((uri, _) => Json(WindowWeather(uri)));
        var clock = new ForecastClock(Start);
        var client = new OpenMeteoSolarHistoryClient(new OpenMeteoJsonReader(new HttpClient(handler), clock), clock);

        var result = await client.ReadAsync(new() { TimeZoneId = "UTC" }, today, today.AddDays(30),
            DateOnly.FromDateTime(today.AddDays(29).Date), default);

        Assert.Equal(today, result.Samples[0].Timestamp);
        Assert.Equal(today.AddDays(16).AddHours(-2), result.Samples[^1].Timestamp);
        Assert.Equal(16 * 24 - 1, result.Samples.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, uri => Assert.Contains("end_date=" + today.AddDays(15).ToString("yyyy-MM-dd"), uri.Query));
        Assert.Null(result.Sunrise);
        Assert.Null(result.NextSunrise);
    }

    [Fact]
    public async Task ProductionBeyondForecastHorizonReturnsNoWeatherWithoutIssuingAnInvalidRequest()
    {
        var handler = new RoutedHttpHandler((_, _) => throw new InvalidOperationException("No request expected."));
        var clock = new ForecastClock(Start);
        var client = new OpenMeteoSolarHistoryClient(new OpenMeteoJsonReader(new HttpClient(handler), clock), clock);

        var result = await client.ReadAsync(new(), Start.AddDays(20), Start.AddDays(21),
            DateOnly.FromDateTime(Start.AddDays(20).Date), default);

        Assert.Empty(result.Samples);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CustomHistorySplitsArchivedAndRecentModelsAndBoundsIndividualRequests()
    {
        var today = new DateTimeOffset(Start.UtcDateTime.Date, TimeSpan.Zero);
        var recentBoundary = today.AddDays(-92);
        var requestedFrom = today.AddDays(-120);
        const string apiKey = "server/history?value&other=1";
        var handler = new RoutedHttpHandler((uri, _) => Json(WindowWeather(uri)));
        var clock = new ForecastClock(Start);
        var client = new OpenMeteoSolarHistoryClient(new OpenMeteoJsonReader(new HttpClient(handler), clock), clock);

        var result = await client.ReadAsync(new() { TimeZoneId = "UTC", ApiKey = apiKey }, requestedFrom,
            today.AddDays(1), DateOnly.FromDateTime(today.Date), default);

        Assert.Equal(121 * 24, result.Samples.Count);
        Assert.Equal(requestedFrom, result.Samples[0].Timestamp);
        Assert.Equal(today.AddDays(1).AddHours(-1), result.Samples[^1].Timestamp);
        Assert.Equal(result.Samples.Count, result.Samples.Select(sample => sample.Timestamp).Distinct().Count());
        Assert.Contains(handler.Requests, uri => uri.Host == "customer-historical-forecast-api.open-meteo.com");
        Assert.Contains(handler.Requests, uri => uri.Host == "customer-api.open-meteo.com");
        Assert.All(handler.Requests.Where(uri => !uri.Query.Contains("daily=")), uri =>
        {
            var query = Query(uri);
            var from = DateOnly.Parse(query["start_date"]);
            var through = DateOnly.Parse(query["end_date"]);
            Assert.InRange(through.DayNumber - from.DayNumber, 0, 31);
            if (uri.Host == "customer-historical-forecast-api.open-meteo.com") Assert.True(through <= DateOnly.FromDateTime(recentBoundary.Date));
            else Assert.True(from >= DateOnly.FromDateTime(recentBoundary.Date));
            Assert.Contains("apikey=" + Uri.EscapeDataString(apiKey), uri.Query);
            Assert.DoesNotContain("&other=1", uri.Query);
        });
    }

    [Fact]
    public async Task AstronomyFailureDoesNotDiscardValidGenerationForecast()
    {
        var handler = new RoutedHttpHandler((uri, _) => uri.Query.Contains("daily=")
            ? new(HttpStatusCode.BadRequest) : Json(WindowWeather(uri)));
        var clock = new ForecastClock(Start);
        var client = new OpenMeteoSolarHistoryClient(new OpenMeteoJsonReader(new HttpClient(handler), clock), clock);

        var result = await client.ReadAsync(new() { TimeZoneId = "UTC" }, Start, Start.AddHours(24),
            DateOnly.FromDateTime(Start.Date), default);

        Assert.Equal(24, result.Samples.Count);
        Assert.Null(result.Sunrise);
        Assert.Null(result.Sunset);
        Assert.Null(result.NextSunrise);
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    private static string WindowWeather(Uri uri)
    {
        if (uri.Query.Contains("daily=")) return "{}";
        var query = Query(uri);
        var from = new DateTimeOffset(DateOnly.Parse(query["start_date"]).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(DateOnly.Parse(query["end_date"]).AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var count = (int)(end - from).TotalHours;
        return JsonSerializer.Serialize(new
        {
            hourly_units = new { time = "unixtime", global_tilted_irradiance = "W/m²", temperature_2m = "°C", wind_speed_10m = "m/s", cloud_cover = "%" },
            hourly = new { time = Enumerable.Range(0, count).Select(hour => from.AddHours(hour).ToUnixTimeSeconds()),
                global_tilted_irradiance = Enumerable.Repeat(500, count), temperature_2m = Enumerable.Repeat(20, count),
                wind_speed_10m = Enumerable.Repeat(1, count), cloud_cover = Enumerable.Repeat(0, count) }
        });
    }

    [Fact]
    public async Task MeanRadiationTimestampBecomesPreviousHourStartWithMatchingEndpointWeather()
    {
        var handler = new RoutedHttpHandler((uri, _) => Json(Weather(gti: IsRoof1(uri)
            ? [100, 200, 300, 400, 500, 600] : [10, 20, 30, 40, 50, 60])));
        var result = await Client(handler).ReadAsync(new(), Start, Start.AddHours(3), default);

        Assert.Equal(new[] { Start, Start.AddHours(1), Start.AddHours(2) }, result.Select(point => point.Timestamp));
        Assert.Equal(new[] { 300d, 400, 500 }, result.Select(point => point.Roof1Gti));
        Assert.Equal(new[] { 30d, 40, 50 }, result.Select(point => point.Roof2Gti));
        Assert.Equal(22, result[0].AirTemperatureC);
        Assert.Equal(2, result[0].WindSpeedMs);
        Assert.Equal(20, result[0].CloudCoverPercent);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(handler.Requests, uri => uri.Query.Contains("azimuth=50"));
        Assert.Contains(handler.Requests, uri => uri.Query.Contains("azimuth=-130"));
        Assert.All(handler.Requests, uri =>
        {
            Assert.Equal("api.open-meteo.com", uri.Host);
            Assert.Equal("/v1/forecast", uri.AbsolutePath);
            Assert.Contains("hourly=global_tilted_irradiance%2Ctemperature_2m%2Cwind_speed_10m%2Ccloud_cover", uri.Query);
            Assert.DoesNotContain("instant", uri.Query);
            Assert.DoesNotContain("minutely", uri.Query);
            Assert.Contains("models=best_match", uri.Query);
            Assert.Contains("tilt=25", uri.Query);
            Assert.Contains("timezone=UTC", uri.Query);
            Assert.Contains("timeformat=unixtime", uri.Query);
            Assert.Contains("wind_speed_unit=ms", uri.Query);
        });
    }

    [Fact]
    public async Task MidnightExclusiveEndStillRequestsItsMeanRadiationRow()
    {
        var start = new DateTimeOffset(2026, 9, 18, 23, 0, 0, TimeSpan.Zero);
        var handler = new RoutedHttpHandler((_, _) => Json(Weather(hours: [12, 13, 14], gti: [100, 250, 800])));
        var result = await Client(handler).ReadAsync(new(), start, start.AddHours(1), default);

        var point = Assert.Single(result);
        Assert.Equal(start, point.Timestamp);
        Assert.Equal(250, point.Roof1Gti);
        Assert.All(handler.Requests, uri =>
        {
            Assert.Contains("start_date=2026-09-18", uri.Query);
            Assert.Contains("end_date=2026-09-19", uri.Query);
        });
    }

    [Fact]
    public async Task OffsetBoundariesAreConvertedToUtcBeforeSelectingRequestDates()
    {
        var start = new DateTimeOffset(2026, 9, 19, 1, 0, 0, TimeSpan.FromHours(2));
        var handler = new RoutedHttpHandler((_, _) => Json(Weather(hours: [13], gti: [250])));
        var result = await Client(handler).ReadAsync(new(), start, start.AddHours(1), default);

        Assert.Equal(new DateTimeOffset(2026, 9, 18, 23, 0, 0, TimeSpan.Zero), Assert.Single(result).Timestamp);
        Assert.All(handler.Requests, uri => Assert.Contains("start_date=2026-09-18", uri.Query));
    }

    [Fact]
    public async Task EachRoofUsesItsOwnGeometryWithInvariantQueryNumbers()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            var options = new SolarEstimateOptions { Roof1Tilt = 27.5, Roof2Tilt = 16.25, Roof1Azimuth = 180, Roof2Azimuth = 90 };
            var handler = new RoutedHttpHandler((_, _) => Json(Weather()));
            await Client(handler).ReadAsync(options, Start, Start.AddHours(1), default);

            Assert.Contains(handler.Requests, uri => uri.Query.Contains("tilt=27.5") && uri.Query.Contains("azimuth=0"));
            Assert.Contains(handler.Requests, uri => uri.Query.Contains("tilt=16.25") && uri.Query.Contains("azimuth=-90"));
            Assert.All(handler.Requests, uri =>
            {
                Assert.Contains("latitude=50.095278", uri.Query);
                Assert.Contains("longitude=20.070278", uri.Query);
            });
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(2001d)]
    public async Task InvalidValueInOneRoofLeavesGapWithoutMovingOtherHours(double? invalid)
    {
        var handler = new RoutedHttpHandler((uri, _) => Json(Weather(hours: [1, 2, 3],
            gti: IsRoof1(uri) ? [300, invalid, 500] : [30, 40, 50])));
        var result = await Client(handler).ReadAsync(new(), Start, Start.AddHours(3), default);

        Assert.Equal(new[] { Start, Start.AddHours(2) }, result.Select(point => point.Timestamp));
        Assert.Equal(500, result[1].Roof1Gti);
        Assert.Equal(50, result[1].Roof2Gti);
    }

    [Fact]
    public async Task OnlyCommonHourTimestampsAreJoinedAcrossRoofs()
    {
        var handler = new RoutedHttpHandler((uri, _) => Json(IsRoof1(uri)
            ? Weather(hours: [1, 2, 3], gti: [300, 400, 500])
            : Weather(hours: [2, 3, 4], gti: [10, 20, 30])));
        var result = await Client(handler).ReadAsync(new(), Start, Start.AddHours(3), default);

        Assert.Equal(new[] { Start.AddHours(1), Start.AddHours(2) }, result.Select(point => point.Timestamp));
        Assert.Equal(400, result[0].Roof1Gti);
        Assert.Equal(10, result[0].Roof2Gti);
        Assert.Equal(20, result[1].Roof2Gti);
    }

    [Fact]
    public async Task MissingTimelineHourRemainsGap()
    {
        var handler = new RoutedHttpHandler((_, _) => Json(Weather(hours: [1, 3], gti: [300, 500])));
        var result = await Client(handler).ReadAsync(new(), Start, Start.AddHours(3), default);

        Assert.Equal(new[] { Start, Start.AddHours(2) }, result.Select(point => point.Timestamp));
    }

    [Fact]
    public async Task TruncatedRadiationDoesNotCreateZeroOrCarryPreviousValue()
    {
        var handler = new RoutedHttpHandler((_, _) => Json(Weather(hours: [1, 2, 3], gti: [300])));
        var result = await Client(handler).ReadAsync(new(), Start, Start.AddHours(3), default);

        Assert.Equal(300, Assert.Single(result).Roof1Gti);
    }

    [Fact]
    public async Task ModelledNighttimeZeroIsValid()
    {
        var handler = new RoutedHttpHandler((_, _) => Json(Weather(hours: [1], gti: [0])));
        var result = await Client(handler).ReadAsync(new(), Start, Start.AddHours(1), default);

        Assert.Equal(0, Assert.Single(result).Roof1Gti);
    }

    [Fact]
    public async Task UnavailableOptionalWeatherRemainsNullable()
    {
        var handler = new RoutedHttpHandler((_, _) => Json(Weather(temperatureUnit: "°F", windUnit: "km/h", cloudUnit: "fraction")));
        var result = await Client(handler).ReadAsync(new(), Start, Start.AddHours(1), default);

        var point = Assert.Single(result);
        Assert.Null(point.AirTemperatureC);
        Assert.Null(point.WindSpeedMs);
        Assert.Null(point.CloudCoverPercent);
        Assert.Equal(300, point.Roof1Gti);
    }

    [Fact]
    public async Task MissingOrOutOfRangeWeatherCannotBecomeZero()
    {
        var handler = new RoutedHttpHandler((_, _) => Json(Weather(hours: [1], gti: [300], temperature: [66], wind: [-1], cloud: [])));
        var result = await Client(handler).ReadAsync(new(), Start, Start.AddHours(1), default);

        var point = Assert.Single(result);
        Assert.Null(point.AirTemperatureC);
        Assert.Null(point.WindSpeedMs);
        Assert.Null(point.CloudCoverPercent);
    }

    [Fact]
    public async Task OptionalWeatherCanComeFromOtherRoofOnlyAtSameHour()
    {
        var handler = new RoutedHttpHandler((uri, _) => Json(Weather(hours: [1, 2], gti: [300, 400],
            temperature: IsRoof1(uri) ? [null, 21] : [19, 23])));
        var result = await Client(handler).ReadAsync(new(), Start, Start.AddHours(2), default);

        Assert.Equal(19, result[0].AirTemperatureC);
        Assert.Equal(21, result[1].AirTemperatureC);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("invalid json")]
    public async Task MissingOrMalformedResponseFailsClearly(string response)
    {
        var handler = new RoutedHttpHandler((_, _) => Json(response));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Start, Start.AddHours(1), default));
    }

    [Theory]
    [InlineData("kW/m²", "unixtime")]
    [InlineData("W/m²", "iso8601")]
    public async Task IncorrectRequiredUnitsAreRejected(string radiationUnit, string timeUnit)
    {
        var handler = new RoutedHttpHandler((_, _) => Json(Weather(gtiUnit: radiationUnit, timeUnit: timeUnit)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Start, Start.AddHours(1), default));
    }

    [Theory]
    [InlineData(1d, 1d)]
    [InlineData(2d, 1d)]
    [InlineData(1d, 1.5d)]
    public async Task DuplicateUnorderedOrOffHourTimelineIsRejected(double first, double second)
    {
        var handler = new RoutedHttpHandler((_, _) => Json(Weather(hours: [first, second], gti: [100, 200])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Start, Start.AddHours(3), default));
    }

    [Fact]
    public async Task NoCommonValidHoursIsUnavailableRatherThanEmptySuccessfulHistory()
    {
        var handler = new RoutedHttpHandler((uri, _) => Json(Weather(hours: [1, 2], gti: IsRoof1(uri) ? [300, null] : [null, 400])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(new(), Start, Start.AddHours(2), default));
    }

    [Fact]
    public async Task IncompleteCurrentHourAndFutureHoursAreExcluded()
    {
        var now = DateTimeOffset.UtcNow;
        var currentHour = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerHour, TimeSpan.Zero);
        var hours = new[] { currentHour, currentHour.AddHours(2) }.Select(time => (time - Start).TotalHours).ToArray();
        var handler = new RoutedHttpHandler((_, _) => Json(Weather(hours: hours, gti: [100, 900])));
        var result = await Client(handler).ReadAsync(new(), currentHour.AddHours(-1), currentHour.AddHours(3), default);

        var point = Assert.Single(result);
        Assert.Equal(currentHour.AddHours(-1), point.Timestamp);
        Assert.Equal(100, point.Roof1Gti);
        Assert.True(point.Timestamp.AddHours(1) <= now);
    }

    [Fact]
    public async Task EntirelyFutureRangeSendsNoRequest()
    {
        var tomorrow = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1), TimeSpan.Zero);
        var handler = new RoutedHttpHandler((_, _) => throw new InvalidOperationException("No request expected."));
        var result = await Client(handler).ReadAsync(new(), tomorrow, tomorrow.AddHours(1), default);

        Assert.Empty(result);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(745d)]
    [InlineData(1.5d)]
    public async Task InvalidOrOversizedRangeFailsBeforeAnyRequest(double hours)
    {
        var handler = new RoutedHttpHandler((_, _) => throw new InvalidOperationException("No request expected."));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).ReadAsync(new(), Start, Start.AddHours(hours), default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NonHourStartFailsBeforeAnyRequest()
    {
        var handler = new RoutedHttpHandler((_, _) => throw new InvalidOperationException("No request expected."));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).ReadAsync(new(), Start.AddMinutes(1), Start.AddHours(1), default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExactlyThirtyOneDaysIsAnAcceptedRange()
    {
        var handler = new RoutedHttpHandler((_, _) => Json(Weather()));
        var result = await Client(handler).ReadAsync(new(), Start.AddDays(-31), Start, default);

        Assert.NotEmpty(result);
        Assert.All(handler.Requests, uri =>
        {
            Assert.Contains("start_date=2026-08-18", uri.Query);
            Assert.Contains("end_date=2026-09-18", uri.Query);
        });
    }

    [Fact]
    public async Task RateLimitRetriesAreBoundedAndCanRecover()
    {
        var handler = new RoutedHttpHandler((uri, attempt) => IsRoof1(uri) && attempt < 3
            ? Retry(HttpStatusCode.TooManyRequests, TimeSpan.Zero) : Json(Weather()));
        var result = await Client(handler).ReadAsync(new(), Start, Start.AddHours(1), default);

        Assert.Equal(300, Assert.Single(result).Roof1Gti);
        Assert.Equal(3, handler.Requests.Count(IsRoof1));
        Assert.Equal(1, handler.Requests.Count(uri => !IsRoof1(uri)));
    }

    [Fact]
    public async Task PaidRecentHistoryPreservesForecastDateRangeAndEscapesServerKey()
    {
        const string key = "server/history?value&other=1";
        var handler = new RoutedHttpHandler((_, _) => Json(Weather()));
        await Client(handler).ReadAsync(new() { ApiKey = "\t" + key + "  " }, Start, Start.AddHours(1), default);

        Assert.All(handler.Requests, uri =>
        {
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("customer-api.open-meteo.com", uri.Host);
            Assert.Equal("/v1/forecast", uri.AbsolutePath);
            Assert.Contains("start_date=2026-09-18", uri.Query);
            Assert.Contains("end_date=2026-09-18", uri.Query);
            Assert.Contains("apikey=" + Uri.EscapeDataString(key), uri.Query);
            Assert.DoesNotContain("&other=1", uri.Query);
        });
    }

    [Fact]
    public async Task LongRetryAfterDefersNextReadWithoutLeakingPaidKey()
    {
        const string key = "private/history?key";
        var handler = new RoutedHttpHandler((_, _) => Retry(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(1)));
        var client = Client(handler);
        var options = new SolarEstimateOptions { ApiKey = key };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadAsync(options, Start, Start.AddHours(1), default));
        var sent = handler.Requests.Count;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadAsync(options, Start, Start.AddHours(1), default));

        Assert.Equal(sent, handler.Requests.Count);
        Assert.DoesNotContain(key, error.ToString());
        Assert.DoesNotContain("apikey", error.ToString());
        Assert.All(handler.Requests, uri => Assert.Equal("customer-api.open-meteo.com", uri.Host));
    }

    [Fact]
    public async Task PermanentFailureOnOneRoofDoesNotReturnPartialRoofHistory()
    {
        var handler = new RoutedHttpHandler((uri, _) => IsRoof1(uri) ? new(HttpStatusCode.BadRequest) : Json(Weather()));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).ReadAsync(new(), Start, Start.AddHours(1), default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CancellationBeforeReadSendsNoRequest()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new RoutedHttpHandler((_, _) => Json(Weather()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).ReadAsync(new(), Start, Start.AddHours(1), cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CancellationDuringReadDoesNotRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RoutedHttpHandler((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).ReadAsync(new(), Start, Start.AddHours(1), cancellation.Token));
        Assert.InRange(handler.Requests.Count, 1, 2);
    }

    private static OpenMeteoSolarHistoryClient Client(HttpMessageHandler handler) => new(new OpenMeteoJsonReader(new HttpClient(handler), TimeProvider.System), TimeProvider.System);
    private static bool IsRoof1(Uri uri) => uri.Query.Contains("azimuth=50");

    private static string Weather(double[]? hours = null, double?[]? gti = null, double?[]? temperature = null,
        double?[]? wind = null, double?[]? cloud = null, string gtiUnit = "W/m²", string timeUnit = "unixtime",
        string temperatureUnit = "°C", string windUnit = "m/s", string cloudUnit = "%")
    {
        hours ??= [-1, 0, 1, 2, 3, 4];
        return JsonSerializer.Serialize(new
        {
            hourly_units = new { time = timeUnit, global_tilted_irradiance = gtiUnit,
                temperature_2m = temperatureUnit, wind_speed_10m = windUnit, cloud_cover = cloudUnit },
            hourly = new
            {
                time = hours.Select(hour => Start.AddHours(hour).ToUnixTimeSeconds()),
                global_tilted_irradiance = gti ?? new double?[] { 100, 200, 300, 400, 500, 600 },
                temperature_2m = temperature ?? hours.Select((_, index) => (double?)(20 + index)).ToArray(),
                wind_speed_10m = wind ?? hours.Select((_, index) => (double?)index).ToArray(),
                cloud_cover = cloud ?? hours.Select((_, index) => (double?)(10 * index)).ToArray()
            }
        });
    }

}

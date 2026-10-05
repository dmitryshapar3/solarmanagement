using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeyeSolar.Infrastructure.Settlement;

namespace DeyeSolar.Infrastructure.Tests;

public sealed class PseExportPriceClientTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OfficialUtcEndpointIsExclusiveIntervalEndAndPricesStaySignedDecimal()
    {
        var handler = new Handler((_, _) => Json(Page([
            Row(Start.AddMinutes(15), 699.13m), Row(Start.AddMinutes(30), -17.12345m),
            Row(Start.AddMinutes(45), 0m)])));

        var prices = await Client(handler).ReadAsync(Start, Start.AddHours(1), default);

        Assert.Equal(3, prices.Count);
        Assert.Equal(Start, prices[0].Start);
        Assert.Equal(Start.AddMinutes(15), prices[0].End);
        Assert.Equal(new[] { 699.13m, -17.12345m, 0m }, prices.Select(price => price.PricePlnPerMwh));
        var uri = Assert.Single(handler.Requests);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("api.raporty.pse.pl", uri.Host);
        Assert.Equal("/api/rce-pln", uri.AbsolutePath);
        var query = Uri.UnescapeDataString(uri.Query);
        Assert.Contains("dtime_utc gt '2026-09-28 22:00:00' and dtime_utc le '2026-09-28 23:00:00'", query);
        Assert.Contains("$orderby=dtime_utc", query);
        Assert.Contains("$first=1000", query);
        Assert.Contains("$select=dtime_utc,rce_pln", query);
    }

    [Fact]
    public async Task OffsetInputsNormalizeToUtcAndDoNotDependOnCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            var handler = new Handler((_, _) => Json(Page([Row(Start.AddMinutes(15), 0.01234m)])));
            var prices = await Client(handler).ReadAsync(Start.ToOffset(TimeSpan.FromHours(2)),
                Start.AddMinutes(15).ToOffset(TimeSpan.FromHours(2)), default);

            Assert.Equal(0.01234m, Assert.Single(prices).PricePlnPerMwh);
            Assert.Equal(TimeSpan.Zero, prices[0].Start.Offset);
            Assert.Contains("2026-09-28 22:00:00", Uri.UnescapeDataString(handler.Requests[0].Query));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task FollowingSameOriginNextLinkReturnsAllPagesWithoutMovingTheirTimes()
    {
        const string next = "https://api.raporty.pse.pl/api/rce-pln?$after=opaque-cursor";
        var handler = new Handler((_, index) => Json(index == 0
            ? Page([Row(Start.AddMinutes(15), 1m)], next)
            : Page([Row(Start.AddMinutes(30), 2m)])));

        var result = await Client(handler).ReadAsync(Start, Start.AddMinutes(30), default);

        Assert.Equal(new[] { Start, Start.AddMinutes(15) }, result.Select(price => price.Start));
        Assert.Equal(next, handler.Requests[1].AbsoluteUri);
    }

    [Fact]
    public async Task MissingAndNullPricesStayGapsWhileAnExplicitZeroRemainsValid()
    {
        var missing = Row(Start.AddMinutes(30), null);
        missing.Remove("rce_pln");
        var handler = new Handler((_, _) => Json(Page([
            Row(Start.AddMinutes(15), null), missing, Row(Start.AddMinutes(60), 0m)])));

        var point = Assert.Single(await Client(handler).ReadAsync(Start, Start.AddHours(1), default));

        Assert.Equal(Start.AddMinutes(45), point.Start);
        Assert.Equal(0m, point.PricePlnPerMwh);
    }

    [Fact]
    public async Task EmptyPublicationReturnsNoPrices()
    {
        var handler = new Handler((_, _) => Json(Page([])));
        Assert.Empty(await Client(handler).ReadAsync(Start, Start.AddDays(1), default));
    }

    [Theory]
    [InlineData("2026-03-28T23:00:00Z", 92)]
    [InlineData("2025-10-25T22:00:00Z", 100)]
    public async Task WarsawDstDaysPreserveEveryDistinctUtcQuarter(string startText, int quarters)
    {
        var start = DateTimeOffset.Parse(startText, CultureInfo.InvariantCulture);
        var rows = Enumerable.Range(1, quarters).Select(index => Row(start.AddMinutes(index * 15), index)).ToArray();
        var handler = new Handler((_, _) => Json(Page(rows)));

        var result = await Client(handler).ReadAsync(start, start.AddMinutes(quarters * 15), default);

        Assert.Equal(quarters, result.Count);
        Assert.Equal(quarters, result.Select(price => price.Start).Distinct().Count());
        Assert.Equal(start, result[0].Start);
        Assert.Equal(start.AddMinutes(quarters * 15), result[^1].End);
        Assert.All(result, price => Assert.Equal(TimeSpan.FromMinutes(15), price.End - price.Start));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -15)]
    [InlineData(1, 15)]
    [InlineData(0, 16)]
    [InlineData(0, 529920)]
    public async Task InvalidBoundariesFailBeforeNetwork(int startMinutes, int endMinutes)
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Network must not run."));

        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler)
            .ReadAsync(Start.AddMinutes(startMinutes), Start.AddMinutes(endMinutes), default));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LeapYearRangeOf366DaysIsAccepted()
    {
        var handler = new Handler((_, _) => Json(Page([])));
        Assert.Empty(await Client(handler).ReadAsync(Start, Start.AddDays(366), default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Full366CalendarDateRangeMayContainAnExtraElapsedHourAtDstBoundary()
    {
        var from = new DateTimeOffset(2025, 10, 26, 0, 0, 0, TimeSpan.FromHours(2));
        var until = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.FromHours(1));
        Assert.Equal(366, new DateOnly(2026, 10, 27).DayNumber - new DateOnly(2025, 10, 26).DayNumber);
        Assert.Equal(TimeSpan.FromDays(366) + TimeSpan.FromHours(1), until - from);
        var handler = new Handler((_, _) => Json(Page([Row(until, 27m)])));

        var point = Assert.Single(await Client(handler).ReadAsync(from, until, default));

        Assert.Equal(new DateTimeOffset(2026, 10, 26, 23, 0, 0, TimeSpan.Zero), point.End);
        Assert.Equal(27m, point.PricePlnPerMwh);
    }

    [Theory]
    [InlineData("2026-09-28 22:00:00")]
    [InlineData("2026-09-28 23:15:00")]
    [InlineData("2026-09-28 22:16:00")]
    [InlineData("2026-09-28 22:15:00+02:00")]
    [InlineData("2026-09-28T22:15:00Z")]
    [InlineData(null)]
    public async Task InvalidOrNeighbourIntervalCannotBePriced(string? timestamp)
    {
        var row = Row(Start.AddMinutes(15), 100m);
        row["dtime_utc"] = timestamp;
        var handler = new Handler((_, _) => Json(Page([row])));

        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(Start, Start.AddHours(1), default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LocalOnlyTimestampIsNeverTreatedAsUtc()
    {
        var row = Row(Start.AddMinutes(15), 100m);
        row.Remove("dtime_utc");
        row["dtime"] = "2026-09-29 00:15:00";
        var handler = new Handler((_, _) => Json(Page([row])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(Start, Start.AddHours(1), default));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(0)]
    public async Task DuplicateOrReversedTimestampAcrossPagesFailsInsteadOfDoubleCounting(int secondMinute)
    {
        var handler = new Handler((_, index) => Json(index == 0
            ? Page([Row(Start.AddMinutes(15), 1m)], "https://api.raporty.pse.pl/api/rce-pln?$after=next")
            : Page([Row(Start.AddMinutes(secondMinute), 999m)])));

        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(Start, Start.AddHours(1), default));
    }

    [Theory]
    [InlineData("\"123.45\"")]
    [InlineData("1e100")]
    [InlineData("true")]
    public async Task InvalidPriceIsNotCoercedOrRounded(string priceJson)
    {
        var json = "{\"value\":[{\"dtime_utc\":\"2026-09-28 22:15:00\",\"rce_pln\":" + priceJson + "}]}";
        var handler = new Handler((_, _) => Json(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(Start, Start.AddHours(1), default));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"value\":null}")]
    [InlineData("[]")]
    public async Task InvalidPageFailsWithoutRetryingOrTreatingItAsEmpty(string json)
    {
        var handler = new Handler((_, _) => Json(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(Start, Start.AddHours(1), default));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://untrusted.example/api/rce-pln")]
    [InlineData("http://api.raporty.pse.pl/api/rce-pln")]
    [InlineData("https://api.raporty.pse.pl:444/api/rce-pln")]
    [InlineData("https://api.raporty.pse.pl/api/other")]
    [InlineData("https://user:password@api.raporty.pse.pl/api/rce-pln")]
    [InlineData("https://api.raporty.pse.pl/api/rce-pln#fragment")]
    [InlineData("/api/rce-pln?$after=next")]
    public async Task UntrustedPaginationIsRejectedBeforeASecondRequest(string next)
    {
        var handler = new Handler((_, _) => Json(Page([], next)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(Start, Start.AddHours(1), default));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://untrusted.example/api/rce-pln")]
    [InlineData("http://api.raporty.pse.pl/api/rce-pln")]
    [InlineData("https://api.raporty.pse.pl/api/other")]
    public async Task RedirectedResponseCannotSupplyAuthoritativePrices(string responseUrl)
    {
        var handler = new Handler((_, _) =>
        {
            var response = Json(Page([Row(Start.AddMinutes(15), 999m)]));
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, responseUrl);
            return response;
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler)
            .ReadAsync(Start, Start.AddHours(1), default));

        Assert.Single(handler.Requests);
        Assert.DoesNotContain(responseUrl, exception.ToString());
    }

    [Fact]
    public async Task RepeatedPageLinkAndTooManyPagesAreBounded()
    {
        var repeated = new Handler((uri, _) => Json(Page([], uri.AbsoluteUri)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(repeated).ReadAsync(Start, Start.AddHours(1), default));
        Assert.Single(repeated.Requests);

        var endless = new Handler((_, index) => Json(Page([], "https://api.raporty.pse.pl/api/rce-pln?$after=" + index)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(endless).ReadAsync(Start, Start.AddDays(366), default));
        Assert.Equal(40, endless.Requests.Count);
    }

    [Fact]
    public async Task OversizedPageFailsBeforeReadingItsRows()
    {
        var rows = Enumerable.Range(1, 1001).Select(index => Row(Start.AddMinutes(index * 15), index)).ToArray();
        var handler = new Handler((_, _) => Json(Page(rows)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(handler).ReadAsync(Start, Start.AddDays(11), default));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task TransientResponseRetriesThenReturnsOnlySuccessfulPublication(int status)
    {
        var handler = new Handler((_, index) => index < 2 ? Failure(status, TimeSpan.Zero)
            : Json(Page([Row(Start.AddMinutes(15), -1m)])));

        Assert.Equal(-1m, Assert.Single(await Client(handler).ReadAsync(Start, Start.AddHours(1), default)).PricePlnPerMwh);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task NetworkFailureHasBoundedRetriesAndSafeError()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("untrusted response details"));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).ReadAsync(Start, Start.AddHours(1), default));
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain("untrusted", exception.ToString());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task PermanentFailureDoesNotRetryOrExposeResponseBody(int status)
    {
        var handler = new Handler((_, _) => new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("untrusted response details") });
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).ReadAsync(Start, Start.AddHours(1), default));
        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        Assert.DoesNotContain("untrusted", exception.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LongRetryAfterSurvivesNextCallWithoutSleepingOrContactingProvider()
    {
        var handler = new Handler((_, _) => Failure(429, TimeSpan.FromMinutes(30)));
        var client = Client(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadAsync(Start, Start.AddHours(1), default));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadAsync(Start, Start.AddHours(1), default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CancellationBeforeCallAndBetweenPagesStopsAllFurtherRequests()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var unused = new Handler((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(unused).ReadAsync(Start, Start.AddHours(1), cancelled.Token));
        Assert.Empty(unused.Requests);

        using var duringCall = new CancellationTokenSource();
        var handler = new Handler((_, _) =>
        {
            duringCall.Cancel();
            return Json(Page([], "https://api.raporty.pse.pl/api/rce-pln?$after=next"));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).ReadAsync(Start, Start.AddHours(1), duringCall.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CallerCancellationInterruptsRetryDelay()
    {
        var handler = new Handler((_, _) => Failure(429, TimeSpan.FromSeconds(30)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).ReadAsync(Start, Start.AddHours(1), cancellation.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task HttpClientTimeoutIsRetriedOnlyThreeTimes()
    {
        var handler = new TimeoutHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(20) };
        await Assert.ThrowsAsync<HttpRequestException>(() => new PseExportPriceClient(new PseJsonReader(http, TimeProvider.System)).ReadAsync(Start, Start.AddHours(1), default));
        Assert.Equal(3, handler.Calls);
    }

    private static Dictionary<string, object?> Row(DateTimeOffset end, decimal? price) => new()
    {
        ["dtime_utc"] = end.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        ["rce_pln"] = price
    };

    private static string Page(Dictionary<string, object?>[] rows, string? next = null) =>
        JsonSerializer.Serialize(new { value = rows, nextLink = next });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Failure(int status, TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private static PseExportPriceClient Client(Handler handler) => new(new PseJsonReader(new HttpClient(handler), TimeProvider.System));

    private sealed class Handler(Func<Uri, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            return Task.FromResult(response(uri, Requests.Count - 1));
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using SolarManagement.Http;

namespace DeyeSolar.Infrastructure.Tests;

public sealed class HttpReadPolicyTests
{
    [Fact]
    public async Task ServerCooldownSurvivesRefreshesAndExpiresUsingTheInjectedClock()
    {
        var clock = new Clock();
        var handler = new Handler(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)) } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
        using var http = new HttpClient(handler);
        var reader = new RetryingJsonReader(http, new("fixture"), clock);
        var uri = new Uri("https://provider.test/data?apikey=private-key");
        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync(uri, default));
        Assert.Equal(HttpStatusCode.TooManyRequests, failed.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync(uri, default));
        Assert.Equal(1, handler.Calls);
        clock.Now += TimeSpan.FromMinutes(3);
        using var result = await reader.ReadAsync(uri, default);
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, handler.Calls);
        Assert.DoesNotContain("private-key", failed.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedSuccessIsTerminalEvenWhenLengthIsNotDeclared(bool declared)
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = declared
            ? new ByteArrayContent(new byte[65]) : new StreamContent(new NonSeekableStream(new byte[65])) });
        using var http = new HttpClient(handler);
        var reader = new RetryingJsonReader(http, new("fixture", MaximumResponseBytes: 64), TimeProvider.System);
        await Assert.ThrowsAsync<ResponseTooLargeException>(() => reader.ReadAsync(new("https://provider.test/data"), default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationNeverStartsARequest()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var reader = new RetryingJsonReader(http, new("fixture"), TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(new("https://provider.test/data"), cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class Handler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response(++Calls));
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-05T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DeyeSolar.Infrastructure.Tests;

internal sealed class RoutedHttpHandler(Func<Uri, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, int> _attempts = new();
    public ConcurrentQueue<Uri> Requests { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        Requests.Enqueue(uri);
        return Task.FromResult(respond(uri, _attempts.AddOrUpdate(uri.ToString(), 1, (_, count) => count + 1)));
    }
}

internal static class HttpResponses
{
    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Retry(HttpStatusCode status, TimeSpan delay)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }
}

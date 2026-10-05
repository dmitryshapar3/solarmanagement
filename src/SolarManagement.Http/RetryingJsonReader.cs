using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace SolarManagement.Http;

public interface IJsonDocumentReader
{
    Task<JsonDocument> ReadAsync(Uri uri, CancellationToken ct);
}

public sealed record JsonReadPolicy(string Source, int MaxDepth = 64, int MaximumResponseBytes = 4 * 1024 * 1024,
    Func<Uri, bool>? IsResponseUriAllowed = null);

/// <summary>Retries idempotent reads only; caller cancellation, body bounds and host cooldowns are shared policy.</summary>
public sealed class RetryingJsonReader(HttpClient http, JsonReadPolicy policy, TimeProvider timeProvider) : IJsonDocumentReader
{
    private readonly TimeProvider _clock = timeProvider;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryNotBefore = new(StringComparer.OrdinalIgnoreCase);
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    public async Task<JsonDocument> ReadAsync(Uri uri, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (_retryNotBefore.TryGetValue(uri.Host, out var deadline) && deadline > _clock.GetUtcNow())
                throw new HttpRequestException($"{policy.Source} Retry-After window is still active; refresh deferred.");
            HttpStatusCode? status = null;
            TimeSpan? retryAfter = null;
            using var timeout = new CancellationTokenSource(RequestTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                if (policy.IsResponseUriAllowed is { } allows && response.RequestMessage?.RequestUri is { } responseUri && !allows(responseUri))
                    throw new InvalidDataException($"{policy.Source} response was redirected away from its authoritative endpoint.");
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await BoundedHttpContent.ReadBytesAsync(response.Content, policy.MaximumResponseBytes, linked.Token).ConfigureAwait(false);
                    return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = policy.MaxDepth });
                }
                status = response.StatusCode;
                retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - _clock.GetUtcNow());
            }
            catch (ResponseTooLargeException) { throw; }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException($"{policy.Source} request canceled.", ct);
            }
            catch (HttpRequestException) { } // Never expose URLs, query credentials or inner transport errors.
            catch (IOException) { }
            catch (JsonException) { throw new InvalidDataException($"{policy.Source} returned malformed JSON."); }
            ct.ThrowIfCancellationRequested();
            var transient = status is null or HttpStatusCode.TooManyRequests || (int)status.Value >= 500;
            if (transient && retryAfter > MaximumRetryDelay)
            {
                var next = _clock.GetUtcNow() + retryAfter.Value;
                _retryNotBefore.AddOrUpdate(uri.Host, next, (_, current) => current > next ? current : next);
            }
            if (!transient || attempt == MaximumAttempts - 1 || retryAfter > MaximumRetryDelay)
                throw new HttpRequestException($"{policy.Source} request failed; refresh deferred.", null, status);
            var delay = retryAfter.HasValue ? (retryAfter.Value < TimeSpan.Zero ? TimeSpan.Zero : retryAfter.Value)
                : TimeSpan.FromMilliseconds(250 * (1 << attempt));
            await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
        }
        throw new HttpRequestException($"{policy.Source} request failed.");
    }
}

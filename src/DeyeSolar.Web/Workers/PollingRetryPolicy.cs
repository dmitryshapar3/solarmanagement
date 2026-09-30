using System.Net;

namespace DeyeSolar.Web.Workers;

internal static class PollingRetryPolicy
{
    internal const int DefaultMaxAttempts = 3;

    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(10);

    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken stoppingToken,
        Action<Exception, int, TimeSpan>? onRetry = null,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? initialDelay = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "At least one attempt is required.");

        var nextDelay = initialDelay ?? DefaultInitialDelay;
        if (nextDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialDelay), "Retry delay cannot be negative.");

        for (var attempt = 1; ; attempt++)
        {
            stoppingToken.ThrowIfCancellationRequested();

            try
            {
                return await operation(stoppingToken);
            }
            catch (Exception ex) when (
                attempt < maxAttempts &&
                IsTransientNetworkFailure(ex, stoppingToken))
            {
                onRetry?.Invoke(ex, attempt, nextDelay);

                if (nextDelay > TimeSpan.Zero)
                    await Task.Delay(nextDelay, stoppingToken);

                nextDelay = TimeSpan.FromMilliseconds(Math.Min(
                    nextDelay.TotalMilliseconds * 2,
                    MaximumDelay.TotalMilliseconds));
            }
        }
    }

    private static bool IsTransientNetworkFailure(Exception exception, CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
            return false;

        if (exception is OperationCanceledException)
            return true;

        if (exception is not HttpRequestException httpException)
            return false;

        return httpException.StatusCode is null or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
    }
}

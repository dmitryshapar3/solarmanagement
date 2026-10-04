using System.Net;
using DeyeSolar.Web.Workers;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Tests;

public class PollingRetryPolicyTests
{
    [Theory]
    [InlineData(IntegrationFailureKind.Transient, 3)]
    [InlineData(IntegrationFailureKind.RateLimited, 3)]
    [InlineData(IntegrationFailureKind.Authentication, 1)]
    [InlineData(IntegrationFailureKind.InvalidResponse, 1)]
    [InlineData(IntegrationFailureKind.ProviderRejected, 1)]
    public async Task WorkerCategoriesRetryOnlyTransientReadFailures(IntegrationFailureKind kind, int expected)
    {
        var attempts = 0;
        await Assert.ThrowsAsync<IntegrationOperationException>(() => PollingRetryPolicy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            return Task.FromException<int>(new IntegrationOperationException(kind));
        }, default, initialDelay: TimeSpan.Zero));
        Assert.Equal(expected, attempts);
    }
    [Fact]
    public async Task ExecuteAsync_RetriesHttpTimeoutAndEventuallyReturnsResult()
    {
        var attempts = 0;

        var result = await PollingRetryPolicy.ExecuteAsync(
            _ => ++attempts < 3
                ? Task.FromException<int>(new TaskCanceledException("HTTP request timed out."))
                : Task.FromResult(42),
            CancellationToken.None,
            initialDelay: TimeSpan.Zero);

        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_StopsAfterConfiguredNumberOfAttempts()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<TaskCanceledException>(() => PollingRetryPolicy.ExecuteAsync<int>(
            _ =>
            {
                attempts++;
                return Task.FromException<int>(new TaskCanceledException("HTTP request timed out."));
            },
            CancellationToken.None,
            maxAttempts: 3,
            initialDelay: TimeSpan.Zero));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryApplicationShutdown()
    {
        using var stopping = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PollingRetryPolicy.ExecuteAsync<int>(
            token =>
            {
                attempts++;
                stopping.Cancel();
                return Task.FromCanceled<int>(token);
            },
            stopping.Token,
            maxAttempts: 3,
            initialDelay: TimeSpan.Zero));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesTransientHttpRequestFailure()
    {
        var attempts = 0;

        var result = await PollingRetryPolicy.ExecuteAsync(
            _ => ++attempts == 1
                ? Task.FromException<int>(new HttpRequestException("Connection reset."))
                : Task.FromResult(7),
            CancellationToken.None,
            initialDelay: TimeSpan.Zero);

        Assert.Equal(7, result);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ExecuteAsync_RetriesTransientServerStatus(HttpStatusCode statusCode)
    {
        var attempts = 0;

        var result = await PollingRetryPolicy.ExecuteAsync(
            _ => ++attempts == 1
                ? Task.FromException<int>(new HttpRequestException("Temporary DeyeCloud failure.", null, statusCode))
                : Task.FromResult(9),
            CancellationToken.None,
            initialDelay: TimeSpan.Zero);

        Assert.Equal(9, result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryPermanentHttpStatus()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => PollingRetryPolicy.ExecuteAsync<int>(
            _ =>
            {
                attempts++;
                return Task.FromException<int>(new HttpRequestException(
                    "Authentication failed.",
                    null,
                    HttpStatusCode.Unauthorized));
            },
            CancellationToken.None,
            initialDelay: TimeSpan.Zero));

        Assert.Equal(1, attempts);
    }
}

using System.Collections.Concurrent;
using System.Net;
using DeyeSolar.Infrastructure.Solar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeyeSolar.Infrastructure.Tests;

public sealed class OpenMeteoRegistrationTests
{
    [Fact]
    public void EveryProviderClientDisablesRedirectsBeforeSendingAQueryKey()
    {
        var services = new ServiceCollection();
        services.AddOpenMeteoSolarClients();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        foreach (var type in ClientTypes)
        {
            var handler = factory.CreateHandler(type.Name);
            while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
            Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FactoryLoggingCannotRecordProviderUrlOrApiKey(int clientIndex)
    {
        const string key = "do-not-log-this-server-key";
        var logger = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logger));
        services.AddOpenMeteoSolarClients();
        services.AddHttpClient(ClientTypes[clientIndex].Name)
            .ConfigurePrimaryHttpMessageHandler(() => new RejectedRequestHandler());
        services.AddHttpClient("unprotected-control")
            .ConfigurePrimaryHttpMessageHandler(() => new RejectedRequestHandler());
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var control = await factory.CreateClient("unprotected-control").GetAsync("https://example.test/logging-control");
        Assert.Contains(logger.Messages, message => message.Contains("/logging-control"));
        var client = factory.CreateClient(ClientTypes[clientIndex].Name);
        using var response = await client.GetAsync("https://customer-api.open-meteo.com/v1/forecast?apikey=" + key);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(key) || message.Contains("apikey"));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("/v1/forecast"));
    }

    private static readonly Type[] ClientTypes =
        [typeof(OpenMeteoCurrentSolarClient), typeof(OpenMeteoSolarHistoryClient), typeof(OpenMeteoSolarClient)];

    private sealed class RejectedRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);
        public void Dispose() { }
    }

    private sealed class RecordingLogger(ConcurrentBag<string> messages) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
    }
}

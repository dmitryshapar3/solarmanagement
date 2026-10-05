using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SolarManagement.Http;

namespace DeyeSolar.Infrastructure.Solar;

public static class OpenMeteoServiceCollectionExtensions
{
    public static IServiceCollection AddOpenMeteoSolarClients(this IServiceCollection services,
        Action<IHttpClientBuilder>? configureTransport = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        // Query credentials must never appear in factory logs or follow redirects.
        var transport = services.AddHttpClient<IOpenMeteoJsonReader, OpenMeteoJsonReader>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(PublicHttpTransport.CreateHandler);
        configureTransport?.Invoke(transport);
        services.AddTransient<OpenMeteoCurrentSolarClient>();
        services.AddTransient<OpenMeteoSolarHistoryClient>();
        services.AddTransient<OpenMeteoSolarClient>();
        return services;
    }
}

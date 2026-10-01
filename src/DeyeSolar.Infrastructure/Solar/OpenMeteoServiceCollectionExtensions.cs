using Microsoft.Extensions.DependencyInjection;

namespace DeyeSolar.Infrastructure.Solar;

public static class OpenMeteoServiceCollectionExtensions
{
    public static IServiceCollection AddOpenMeteoSolarClients(this IServiceCollection services)
    {
        // Open-Meteo requires apikey in the query. Avoid URL logging and never forward
        // a customer URL/key through a redirect, including when using the satellite source.
        services.AddHttpClient<OpenMeteoCurrentSolarClient>().RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);
        services.AddHttpClient<OpenMeteoSolarHistoryClient>().RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);
        services.AddHttpClient<OpenMeteoSolarClient>().RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);
        return services;
    }

    private static HttpMessageHandler CreateHandler() => new HttpClientHandler { AllowAutoRedirect = false };
}

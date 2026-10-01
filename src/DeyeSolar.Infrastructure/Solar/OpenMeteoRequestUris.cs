using DeyeSolar.Domain.Options;

namespace DeyeSolar.Infrastructure.Solar;

/// <summary>Fixed provider endpoints keep the server-only key away from arbitrary hosts.</summary>
internal static class OpenMeteoRequestUris
{
    public static Uri Forecast(SolarEstimateOptions options, IReadOnlyDictionary<string, string> parameters) =>
        Build("api.open-meteo.com", "forecast", options.ApiKey, parameters);

    public static Uri Satellite(SolarEstimateOptions options, IReadOnlyDictionary<string, string> parameters) =>
        Build("satellite-api.open-meteo.com", "archive", options.ApiKey, parameters);

    private static Uri Build(string publicHost, string endpoint, string? apiKey,
        IReadOnlyDictionary<string, string> parameters)
    {
        var key = apiKey?.Trim();
        var commercial = !string.IsNullOrEmpty(key);
        var host = commercial ? "customer-" + publicHost : publicHost;
        var query = parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value));
        if (commercial) query = query.Append("apikey=" + Uri.EscapeDataString(key!));
        return new Uri($"https://{host}/v1/{endpoint}?" + string.Join("&", query));
    }
}

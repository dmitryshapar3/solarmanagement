using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;

namespace DeyeSolar.Infrastructure.Solar;

/// <summary>
/// DWD/EUMETSAT MTG observations via Open-Meteo, not a current-weather forecast.
/// The 0.025 degree grid updates every ten minutes, with a nominal twenty-minute delay.
/// GTI includes clouds, an isotropic sky and 20% ground albedo. Weather is a separate model.
/// Documentation: https://open-meteo.com/en/docs/satellite-radiation-api (checked 2026-09-18).
/// Data attribution: Open-Meteo / DWD, CC BY 4.0. The free service is non-commercial only.
/// </summary>
public sealed class OpenMeteoSolarClient(HttpClient httpClient) : ISolarRadiationSource
{
    public const string SatelliteModel = "dwd_sis_europe_africa_v4";
    private const string GtiVariable = "global_tilted_irradiance_instant";
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    // This client is held by the shared source service, so a server cooldown survives
    // later scheduled refreshes without making the worker sleep for many minutes.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryNotBefore = new();

    public async Task<SolarRadiationObservation> ReadAsync(SolarEstimateOptions options,
        DateTimeOffset now, CancellationToken ct)
    {
        options.Validate();
        ct.ThrowIfCancellationRequested();
        var roofTasks = new[]
        {
            ReadRoofAsync(options, options.Roof1Tilt, options.Roof1Azimuth, ct),
            ReadRoofAsync(options, options.Roof2Tilt, options.Roof2Azimuth, ct)
        };
        var roofs = await Task.WhenAll(roofTasks);
        // Intersect actual valid timestamps; neither a future slot nor one roof's older value
        // may be substituted into the other roof's observation.
        var common = roofs[0].Keys.Where(t => t <= now.ToUnixTimeSeconds() && roofs[1].ContainsKey(t))
            .OrderBy(t => t).ToArray();
        if (common.Length == 0)
            throw new InvalidDataException("Open-Meteo returned no simultaneous valid satellite observations.");

        var timestamp = common[^1];
        var recent = common.Where(t => t >= timestamp - 3600)
            .Select(t => (roofs[0][t] * options.Roof1Kwp + roofs[1][t] * options.Roof2Kwp) / options.TotalKwp)
            .ToArray();
        // This is a variability indicator, not a statistical error estimate. Sparse samples
        // must not imply stable weather; nighttime zero irradiance is legitimately stable.
        var mean = recent.Average();
        var variability = recent.Length < 3 ? 0.4
            : mean <= 1 ? 0 : Math.Clamp((recent.Max() - recent.Min()) / mean, 0, 2);
        var observationTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        var weather = await ReadWeatherAsync(options, observationTime, ct);
        return new(observationTime, roofs[0][timestamp], roofs[1][timestamp],
            weather?.Temperature, weather?.Wind, weather?.Timestamp, variability);
    }

    private async Task<Dictionary<long, double>> ReadRoofAsync(SolarEstimateOptions options,
        double tilt, double compassAzimuth, CancellationToken ct)
    {
        var parameters = CommonParameters(options);
        parameters["hourly"] = GtiVariable;
        parameters["models"] = SatelliteModel;
        parameters["temporal_resolution"] = "native";
        parameters["tilt"] = Number(tilt);
        parameters["azimuth"] = Number(SolarPowerCalculator.ToOpenMeteoAzimuth(compassAzimuth));
        var host = HasKey(options) ? "customer-satellite-api.open-meteo.com" : "satellite-api.open-meteo.com";
        using var document = await GetJsonAsync(BuildUri(host, "archive", parameters), ct);
        var root = document.RootElement;
        RequireUnits(root, GtiVariable, "W/m²");
        var time = RequireArray(root, "time");
        var values = RequireArray(root, GtiVariable);
        // Enforce DWD's native ten-minute timeline, even if some radiation entries are null.
        // A one-point response cannot establish its temporal resolution.
        if (time.GetArrayLength() < 2)
            throw new InvalidDataException("Open-Meteo satellite timeline has insufficient resolution metadata.");
        var result = new Dictionary<long, double>();
        long? previous = null;
        var index = 0;
        foreach (var item in time.EnumerateArray())
        {
            if (!TryTimestamp(item, out var timestamp) || (previous.HasValue && timestamp - previous.Value != 600))
                throw new InvalidDataException("Open-Meteo satellite timeline is not native ten-minute data.");
            previous = timestamp;
            if (index < values.GetArrayLength() && TryNumber(values[index], 0, 2000, out var gti))
                result[timestamp] = gti;
            index++;
        }
        return result;
    }

    private async Task<WeatherPoint?> ReadWeatherAsync(SolarEstimateOptions options,
        DateTimeOffset observationTime, CancellationToken ct)
    {
        try
        {
            var parameters = CommonParameters(options);
            parameters["hourly"] = "temperature_2m,wind_speed_10m";
            parameters["wind_speed_unit"] = "ms";
            var host = HasKey(options) ? "customer-api.open-meteo.com" : "api.open-meteo.com";
            using var document = await GetJsonAsync(BuildUri(host, "forecast", parameters), ct);
            var root = document.RootElement;
            RequireUnits(root, "temperature_2m", "°C");
            RequireUnits(root, "wind_speed_10m", "m/s");
            var times = RequireArray(root, "time");
            var temperatures = RequireArray(root, "temperature_2m");
            var winds = RequireArray(root, "wind_speed_10m");
            var limit = Math.Min(times.GetArrayLength(), Math.Min(temperatures.GetArrayLength(), winds.GetArrayLength()));
            var latest = observationTime.ToUnixTimeSeconds();
            WeatherPoint? selected = null;
            for (var i = 0; i < limit; i++)
            {
                if (!TryTimestamp(times[i], out var timestamp) || timestamp > latest || timestamp < latest - 5400
                    || !TryNumber(temperatures[i], -80, 65, out var temperature)
                    || !TryNumber(winds[i], 0, 100, out var wind)) continue;
                var date = DateTimeOffset.FromUnixTimeSeconds(timestamp);
                if (selected is null || date > selected.Timestamp)
                    selected = new(date, temperature, wind);
            }
            return selected;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // The caller's model explicitly broadens its assumptions when model weather is
            // absent. Do not invent an API temperature, wind speed or observation timestamp.
            return null;
        }
    }

    internal async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (_retryNotBefore.TryGetValue(uri.Host, out var retryDeadline) && retryDeadline > DateTimeOffset.UtcNow)
                throw new HttpRequestException("Open-Meteo Retry-After window is still active; refresh deferred.");
            HttpStatusCode? failureStatus = null;
            TimeSpan? retryAfter = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                }
                failureStatus = response.StatusCode;
                retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Includes HttpClient.Timeout and this request's twelve-second deadline.
            }
            catch (HttpRequestException)
            {
                // Never propagate an exception containing the URL's optional API key.
            }
            catch (IOException)
            {
                // A connection can fail while reading a successful response body.
            }
            catch (JsonException)
            {
                throw new InvalidDataException("Open-Meteo returned malformed JSON.");
            }
            ct.ThrowIfCancellationRequested();
            var transient = failureStatus is null or HttpStatusCode.TooManyRequests
                || (int)failureStatus.Value >= 500;
            // Persist the complete server deadline, even when it exceeds the worker's
            // ten-minute schedule. Later refreshes are rejected locally until it expires.
            if (transient && retryAfter > MaximumRetryDelay)
            {
                var deadline = DateTimeOffset.UtcNow + retryAfter.Value;
                _retryNotBefore.AddOrUpdate(uri.Host, deadline, (_, current) => current > deadline ? current : deadline);
            }
            if (!transient || attempt == MaximumAttempts - 1 || retryAfter > MaximumRetryDelay)
                throw new HttpRequestException("Open-Meteo request failed; refresh deferred.", null, failureStatus);
            var delay = retryAfter.HasValue ? (retryAfter.Value < TimeSpan.Zero ? TimeSpan.Zero : retryAfter.Value)
                : TimeSpan.FromMilliseconds(250 * (1 << attempt));
            await Task.Delay(delay, ct);
        }
        throw new HttpRequestException("Open-Meteo request failed.");
    }

    private static Dictionary<string, string> CommonParameters(SolarEstimateOptions options)
    {
        var parameters = new Dictionary<string, string>
        {
            ["latitude"] = Number(options.Latitude), ["longitude"] = Number(options.Longitude),
            ["timeformat"] = "unixtime", ["timezone"] = "UTC", ["past_days"] = "1", ["forecast_days"] = "1"
        };
        if (HasKey(options)) parameters["apikey"] = options.ApiKey!;
        return parameters;
    }

    private static bool HasKey(SolarEstimateOptions options) => !string.IsNullOrWhiteSpace(options.ApiKey);
    private static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    private static Uri BuildUri(string host, string endpoint, Dictionary<string, string> parameters) =>
        new($"https://{host}/v1/{endpoint}?" + string.Join("&", parameters.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));

    private static void RequireUnits(JsonElement root, string variable, string expected)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("hourly_units", out var units)
            || units.ValueKind != JsonValueKind.Object || !units.TryGetProperty("time", out var time)
            || time.ValueKind != JsonValueKind.String || time.GetString() != "unixtime"
            || !units.TryGetProperty(variable, out var unit) || unit.ValueKind != JsonValueKind.String
            || unit.GetString() != expected)
            throw new InvalidDataException("Open-Meteo returned missing or unexpected units.");
    }

    private static JsonElement RequireArray(JsonElement root, string variable)
    {
        if (!root.TryGetProperty("hourly", out var hourly) || hourly.ValueKind != JsonValueKind.Object
            || !hourly.TryGetProperty(variable, out var array) || array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Open-Meteo returned an incomplete hourly response.");
        return array;
    }

    private static bool TryTimestamp(JsonElement item, out long timestamp)
    {
        timestamp = 0;
        return item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out timestamp)
            && timestamp is >= -62135596800 and <= 253402300799;
    }

    private static bool TryNumber(JsonElement item, double min, double max, out double number)
    {
        number = 0;
        return item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out number)
            && double.IsFinite(number) && number >= min && number <= max;
    }

    private sealed record WeatherPoint(DateTimeOffset Timestamp, double Temperature, double Wind);
}

using System.Globalization;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;

namespace DeyeSolar.Infrastructure.Solar;

/// <summary>
/// Current model estimate from Open-Meteo Best Match weather, including model cloud effects in GTI.
/// Fifteen-minute output can be interpolated from hourly model data at this site; it is not a
/// satellite observation or a claim of native fifteen-minute model resolution. ICON-D2 explicitly
/// returned no data for these coordinates during the live verification on 2026-09-18.
/// https://open-meteo.com/en/docs — minutely_15, global_tilted_irradiance_instant.
/// </summary>
public sealed class OpenMeteoCurrentSolarClient(HttpClient httpClient) : ISolarRadiationSource
{
    public const string WeatherModel = "best_match";
    private const string GtiVariable = "global_tilted_irradiance_instant";
    // Reuse the bounded deadlines/retries and persistent per-host Retry-After cooldown.
    private readonly OpenMeteoSolarClient _transport = new(httpClient);

    public async Task<SolarRadiationObservation> ReadAsync(SolarEstimateOptions options,
        DateTimeOffset now, CancellationToken ct)
    {
        options.Validate();
        ct.ThrowIfCancellationRequested();
        var roofs = await Task.WhenAll(
            ReadRoofAsync(options, options.Roof1Tilt, options.Roof1Azimuth, ct),
            ReadRoofAsync(options, options.Roof2Tilt, options.Roof2Azimuth, ct));
        var earliest = now.AddMinutes(-75).ToUnixTimeSeconds();
        var latest = now.AddMinutes(75).ToUnixTimeSeconds();
        var samples = roofs[0].Keys.Where(time => time >= earliest && time <= latest && roofs[1].ContainsKey(time))
            .OrderBy(time => time).Select(time =>
            {
                var sw = roofs[0][time];
                var ne = roofs[1][time];
                return new SolarWeatherSample(DateTimeOffset.FromUnixTimeSeconds(time), sw.Gti, ne.Gti,
                    sw.Temperature ?? ne.Temperature, sw.Wind ?? ne.Wind, sw.Cloud ?? ne.Cloud);
            }).ToArray();

        // Keep enough consecutive forecast to interpolate between refreshes, without extrapolating
        // through a missing model interval or silently treating a future sample as an observation.
        var before = samples.LastOrDefault(sample => sample.Timestamp <= now);
        var horizon = samples.FirstOrDefault(sample => sample.Timestamp >= now.AddMinutes(30));
        if (before is null || horizon is null)
            throw new InvalidDataException("Open-Meteo model does not bracket now with sufficient forecast coverage.");
        var coverage = samples.Where(sample => sample.Timestamp >= before.Timestamp && sample.Timestamp <= horizon.Timestamp).ToArray();
        for (var i = 1; i < coverage.Length; i++)
            if (coverage[i].Timestamp - coverage[i - 1].Timestamp > TimeSpan.FromMinutes(15))
                throw new InvalidDataException("Open-Meteo model has a missing fifteen-minute interval.");

        var recent = samples.Where(sample => sample.Timestamp <= now && sample.Timestamp >= now.AddHours(-1))
            .Select(sample => (sample.Roof1Gti * options.Roof1Kwp + sample.Roof2Gti * options.Roof2Kwp) / options.TotalKwp)
            .ToArray();
        var mean = recent.Length == 0 ? 0 : recent.Average();
        var variability = recent.Length < 3 ? 0.4 : mean <= 1 ? 0
            : Math.Clamp((recent.Max() - recent.Min()) / mean, 0, 2);
        var observation = new SolarRadiationObservation(before.Timestamp, before.Roof1Gti, before.Roof2Gti,
            before.AirTemperatureC, before.WindSpeedMs, before.Timestamp, variability)
        {
            Kind = SolarRadiationKind.WeatherModel,
            Forecast = Array.AsReadOnly(samples),
            RetrievedAt = now,
            CloudCoverPercent = before.CloudCoverPercent
        };
        return SolarNowcast.Interpolate(observation, now);
    }

    private async Task<Dictionary<long, RoofWeather>> ReadRoofAsync(SolarEstimateOptions options,
        double tilt, double compassAzimuth, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["latitude"] = Number(options.Latitude), ["longitude"] = Number(options.Longitude),
            ["tilt"] = Number(tilt), ["azimuth"] = Number(SolarPowerCalculator.ToOpenMeteoAzimuth(compassAzimuth)),
            ["models"] = WeatherModel,
            ["minutely_15"] = GtiVariable + ",temperature_2m,wind_speed_10m,cloud_cover",
            ["wind_speed_unit"] = "ms", ["timeformat"] = "unixtime", ["timezone"] = "UTC",
            ["past_minutely_15"] = "5", ["forecast_minutely_15"] = "5"
        };
        using var document = await _transport.GetJsonAsync(OpenMeteoRequestUris.Forecast(options, parameters), ct);
        var root = document.RootElement;
        if (!HasUnit(root, "time", "unixtime") || !HasUnit(root, GtiVariable, "W/m²"))
            throw new InvalidDataException("Open-Meteo model returned missing or unexpected radiation units.");
        var times = ArrayFor(root, "time");
        var gti = ArrayFor(root, GtiVariable);
        if (times.ValueKind != JsonValueKind.Array || times.GetArrayLength() < 2 || gti.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Open-Meteo model returned an incomplete timeline.");
        var temperatures = HasUnit(root, "temperature_2m", "°C") ? ArrayFor(root, "temperature_2m") : default;
        var winds = HasUnit(root, "wind_speed_10m", "m/s") ? ArrayFor(root, "wind_speed_10m") : default;
        var clouds = HasUnit(root, "cloud_cover", "%") ? ArrayFor(root, "cloud_cover") : default;
        var result = new Dictionary<long, RoofWeather>();
        long? previous = null;
        for (var index = 0; index < times.GetArrayLength(); index++)
        {
            var time = times[index];
            if (time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out var timestamp)
                || timestamp is < -62135596800 or > 253402300799
                || (previous.HasValue && timestamp - previous.Value != 900))
                throw new InvalidDataException("Open-Meteo model timeline is not a regular fifteen-minute series.");
            previous = timestamp;
            var irradiance = OptionalNumber(gti, index, 0, 2000);
            if (irradiance is null) continue;
            result[timestamp] = new(irradiance.Value, OptionalNumber(temperatures, index, -80, 65),
                OptionalNumber(winds, index, 0, 100), OptionalNumber(clouds, index, 0, 100));
        }
        return result;
    }

    private static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    private static bool HasUnit(JsonElement root, string variable, string expected) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("minutely_15_units", out var units)
        && units.ValueKind == JsonValueKind.Object && units.TryGetProperty(variable, out var unit)
        && unit.ValueKind == JsonValueKind.String && unit.GetString() == expected;

    private static JsonElement ArrayFor(JsonElement root, string variable) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("minutely_15", out var values)
        && values.ValueKind == JsonValueKind.Object && values.TryGetProperty(variable, out var array)
        && array.ValueKind == JsonValueKind.Array ? array : default;

    private static double? OptionalNumber(JsonElement array, int index, double minimum, double maximum) =>
        array.ValueKind == JsonValueKind.Array && index < array.GetArrayLength()
        && array[index].ValueKind == JsonValueKind.Number && array[index].TryGetDouble(out var value)
        && double.IsFinite(value) && value >= minimum && value <= maximum ? value : null;

    private sealed record RoofWeather(double Gti, double? Temperature, double? Wind, double? Cloud);
}

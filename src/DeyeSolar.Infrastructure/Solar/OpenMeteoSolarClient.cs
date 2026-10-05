using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using static DeyeSolar.Infrastructure.Solar.OpenMeteoSeries;

namespace DeyeSolar.Infrastructure.Solar;

/// <summary>
/// DWD/EUMETSAT MTG observations via Open-Meteo, not a current-weather forecast.
/// The 0.025 degree grid updates every ten minutes, with a nominal twenty-minute delay.
/// GTI includes clouds, an isotropic sky and 20% ground albedo. Weather is a separate model.
/// Documentation: https://open-meteo.com/en/docs/satellite-radiation-api (checked 2026-09-18).
/// Data attribution: Open-Meteo / DWD, CC BY 4.0. The free service is non-commercial only.
/// </summary>
public sealed class OpenMeteoSolarClient(IOpenMeteoJsonReader transport) : ISolarRadiationSource
{
    public const string SatelliteModel = "dwd_sis_europe_africa_v4";
    private const string GtiVariable = "global_tilted_irradiance_instant";
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
        var variability = Variability(recent);
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
        using var document = await transport.ReadAsync(OpenMeteoRequestUris.Satellite(options, parameters), ct);
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
            using var document = await transport.ReadAsync(OpenMeteoRequestUris.Forecast(options, parameters), ct);
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

    private static Dictionary<string, string> CommonParameters(SolarEstimateOptions options)
    {
        var parameters = new Dictionary<string, string>
        {
            ["latitude"] = Number(options.Latitude), ["longitude"] = Number(options.Longitude),
            ["timeformat"] = "unixtime", ["timezone"] = "UTC", ["past_days"] = "1", ["forecast_days"] = "1"
        };
        return parameters;
    }

    private static void RequireUnits(JsonElement root, string variable, string expected)
    {
        if (!HasUnit(root, "hourly", "time", "unixtime") || !HasUnit(root, "hourly", variable, expected))
            throw new InvalidDataException("Open-Meteo returned missing or unexpected units.");
    }
    private static JsonElement RequireArray(JsonElement root, string variable)
    {
        var array = ArrayFor(root, "hourly", variable);
        return array.ValueKind == JsonValueKind.Array ? array
            : throw new InvalidDataException("Open-Meteo returned an incomplete hourly response.");
    }

    private sealed record WeatherPoint(DateTimeOffset Timestamp, double Temperature, double Wind);
}

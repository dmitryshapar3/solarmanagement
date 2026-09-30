using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Services;

/// <summary>Evaluate instantaneous model values at a time, never extrapolate or relabel observations.</summary>
public static class SolarNowcast
{
    public static SolarRadiationObservation Interpolate(SolarRadiationObservation source, DateTimeOffset targetTime)
    {
        if (source.Kind != SolarRadiationKind.WeatherModel || source.Forecast is not { Count: > 0 } points)
            throw new InvalidDataException("A weather-model timeline is required.");
        SolarWeatherSample? before = null, after = null;
        DateTimeOffset? previous = null;
        foreach (var point in points)
        {
            if (previous.HasValue && point.Timestamp <= previous.Value)
                throw new InvalidDataException("Model times must be unique and increasing.");
            previous = point.Timestamp;
            if (point.Timestamp <= targetTime) before = point;
            if (point.Timestamp >= targetTime && after is null) after = point;
        }
        if (before is null || after is null || (after.Timestamp - before.Timestamp).TotalMinutes > 15)
            throw new InvalidDataException("No adjacent model points bracket the requested time.");
        if (!Valid(before.Roof1Gti, 0, 2000) || !Valid(before.Roof2Gti, 0, 2000)
            || !Valid(after.Roof1Gti, 0, 2000) || !Valid(after.Roof2Gti, 0, 2000))
            throw new InvalidDataException("Invalid model irradiance.");
        var fraction = before.Timestamp == after.Timestamp ? 0
            : (targetTime - before.Timestamp).TotalSeconds / (after.Timestamp - before.Timestamp).TotalSeconds;
        double Blend(double a, double b) => a + (b - a) * fraction;
        double? BlendOptional(double? a, double? b, double min, double max) =>
            a.HasValue && b.HasValue && Valid(a.Value, min, max) && Valid(b.Value, min, max)
                ? Blend(a.Value, b.Value) : null;
        var nearby = points.Where(p => Math.Abs((p.Timestamp - targetTime).TotalMinutes) <= 30)
            .Select(p => p.Roof1Gti + p.Roof2Gti).Where(p => Valid(p, 0, 4000)).ToArray();
        var mean = nearby.Length == 0 ? 0 : nearby.Average();
        var variability = nearby.Length < 3 ? 0.4 : mean <= 1 ? 0
            : Math.Clamp((nearby.Max() - nearby.Min()) / mean, 0, 2);
        return source with
        {
            Timestamp = targetTime,
            Roof1Gti = Blend(before.Roof1Gti, after.Roof1Gti),
            Roof2Gti = Blend(before.Roof2Gti, after.Roof2Gti),
            AirTemperatureC = BlendOptional(before.AirTemperatureC, after.AirTemperatureC, -80, 65),
            WindSpeedMs = BlendOptional(before.WindSpeedMs, after.WindSpeedMs, 0, 100),
            WeatherTimestamp = targetTime,
            CloudCoverPercent = BlendOptional(before.CloudCoverPercent, after.CloudCoverPercent, 0, 100),
            RecentVariabilityFraction = variability,
            ModelPeriodStart = before.Timestamp,
            ModelPeriodEnd = after.Timestamp
        };
    }

    private static bool Valid(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}

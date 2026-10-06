using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Services;

public sealed record SolarObservedEnergy(double? EnergyKwh, double CoveredSeconds, double ExpectedSeconds)
{
    public bool Partial => CoveredSeconds < ExpectedSeconds;
}

public static class SolarEnergyIntegration
{
    public static SolarObservedEnergy Integrate(IReadOnlyList<SolarActual> samples, DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) return new(null, 0, 0);
        var relevant = samples.Where(p => p.Timestamp >= start.AddMinutes(-10) && p.Timestamp <= end.AddMinutes(10)
            && p.Basis == SolarPowerBasis.PvDc && double.IsFinite(p.PowerKw) && p.PowerKw >= 0)
            .GroupBy(p => p.Timestamp).Select(g => g.Last()).OrderBy(p => p.Timestamp).ToArray();
        var seconds = 0d;
        var energy = 0d;
        for (var i = 1; i < relevant.Length; i++)
        {
            var left = relevant[i - 1]; var right = relevant[i];
            var duration = (right.Timestamp - left.Timestamp).TotalSeconds;
            if (duration <= 0 || duration > 600) continue;
            var from = left.Timestamp > start ? left.Timestamp : start;
            var to = right.Timestamp < end ? right.Timestamp : end;
            if (to <= from) continue;
            var a = left.PowerKw + (right.PowerKw - left.PowerKw) * (from - left.Timestamp).TotalSeconds / duration;
            var b = left.PowerKw + (right.PowerKw - left.PowerKw) * (to - left.Timestamp).TotalSeconds / duration;
            var covered = (to - from).TotalSeconds;
            energy += (a + b) / 2 * covered / 3600;
            seconds += covered;
        }
        return new(seconds > 0 ? energy : null, seconds, (end - start).TotalSeconds);
    }
}

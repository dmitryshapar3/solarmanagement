using System.Globalization;

namespace DeyeSolar.Web.Components.Ui;

public sealed record SolarDiagramPoint(double X, double Y, double Azimuth, double Elevation, DateTimeOffset At);
public sealed record SolarDiagramDay(DateOnly Date, string TimeZoneId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, IReadOnlyList<IReadOnlyList<SolarDiagramPoint>> Paths,
    SolarDiagramPoint? Now, DateTimeOffset? Sunrise, DateTimeOffset? Sunset, string State);
public sealed record SolarDiagramRoof(int Number, double Capacity, double Tilt, double Azimuth, double Width, double Depth, double CenterX);

/// <summary>
/// NOAA's approximate geometric solar position, clockwise from true north.
/// https://gml.noaa.gov/grad/solcalc/solareqns.PDF
/// Diagram only: no atmospheric refraction, terrain/shadows, irradiance or energy prediction.
/// The horizon represents the sun's centre at 0° elevation, not published apparent sunrise.
/// </summary>
public static class RoofSunGeometry
{
    public const double Center = 160, Radius = 112;
    private const double Rad = Math.PI / 180;

    public static SolarDiagramPoint Position(double latitude, double longitude, DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        var gamma = 2 * Math.PI / (DateTime.IsLeapYear(utc.Year) ? 366 : 365) * (utc.DayOfYear - 1 + (utc.TimeOfDay.TotalHours - 12) / 24);
        var equation = 229.18 * (.000075 + .001868 * Math.Cos(gamma) - .032077 * Math.Sin(gamma) - .014615 * Math.Cos(2 * gamma) - .040849 * Math.Sin(2 * gamma));
        var declination = .006918 - .399912 * Math.Cos(gamma) + .070257 * Math.Sin(gamma) - .006758 * Math.Cos(2 * gamma) + .000907 * Math.Sin(2 * gamma) - .002697 * Math.Cos(3 * gamma) + .00148 * Math.Sin(3 * gamma);
        var solarMinutes = ((utc.TimeOfDay.TotalMinutes + equation + 4 * longitude) % 1440 + 1440) % 1440;
        var hourAngle = (solarMinutes / 4 - 180) * Rad;
        var lat = latitude * Rad;
        var elevation = Math.Asin(Math.Clamp(Math.Sin(lat) * Math.Sin(declination) + Math.Cos(lat) * Math.Cos(declination) * Math.Cos(hourAngle), -1, 1)) / Rad;
        var azimuth = (Math.Atan2(-Math.Cos(declination) * Math.Sin(hourAngle), Math.Cos(lat) * Math.Sin(declination) - Math.Sin(lat) * Math.Cos(declination) * Math.Cos(hourAngle)) / Rad + 360) % 360;
        var distance = Radius * (90 - Math.Clamp(elevation, 0, 90)) / 90;
        return new(Center + Math.Sin(azimuth * Rad) * distance, Center - Math.Cos(azimuth * Rad) * distance, azimuth, elevation, instant);
    }

    public static SolarDiagramDay? Day(double latitude, double longitude, string? timeZoneId, DateTimeOffset instant)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90 || !double.IsFinite(longitude) || longitude is < -180 or > 180 || string.IsNullOrWhiteSpace(timeZoneId)) return null;
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
        var start = Midnight(date, zone); var end = Midnight(date.AddDays(1), zone);
        var samples = new List<SolarDiagramPoint>();
        for (var at = start; at < end; at = at.AddMinutes(5)) samples.Add(Position(latitude, longitude, at));
        samples.Add(Position(latitude, longitude, end));
        var paths = new List<IReadOnlyList<SolarDiagramPoint>>(); var path = new List<SolarDiagramPoint>();
        DateTimeOffset? sunrise = null, sunset = null;
        if (samples[0].Elevation >= 0) path.Add(samples[0]);
        for (var i = 1; i < samples.Count; ++i)
        {
            var before = samples[i - 1]; var next = samples[i];
            if ((before.Elevation >= 0) != (next.Elevation >= 0))
            {
                // Five-minute samples bracket the crossing; bisection gives a stable geometric horizon.
                var lo = before.At; var hi = next.At;
                for (var step = 0; step < 18; ++step)
                {
                    var middle = lo + (hi - lo) / 2;
                    if ((Position(latitude, longitude, middle).Elevation >= 0) == (before.Elevation >= 0)) lo = middle; else hi = middle;
                }
                var crossing = Position(latitude, longitude, lo + (hi - lo) / 2);
                if (next.Elevation >= 0) { sunrise = crossing.At; path.Add(crossing); }
                else { sunset = crossing.At; path.Add(crossing); paths.Add(path.ToArray()); path = []; }
            }
            if (next.Elevation >= 0) path.Add(next);
        }
        if (path.Count > 0) paths.Add(path.ToArray());
        var current = Position(latitude, longitude, instant);
        var state = samples.All(point => point.Elevation >= 0) ? "polar-day" : samples.All(point => point.Elevation < 0) ? "polar-night" : "normal";
        return new(date, zone.Id, start, end, paths, current.Elevation >= 0 ? current : null, sunrise, sunset, state);
    }

    public static IReadOnlyList<SolarDiagramRoof> Roofs(double capacity1, double tilt1, double azimuth1, double capacity2, double tilt2, double azimuth2)
    {
        var valid = new[] { (Number: 1, Capacity: capacity1, Tilt: tilt1, Azimuth: azimuth1), (Number: 2, Capacity: capacity2, Tilt: tilt2, Azimuth: azimuth2) }
            .Where(roof => double.IsFinite(roof.Capacity) && roof.Capacity > 0 && roof.Capacity <= 10000 && double.IsFinite(roof.Tilt) && roof.Tilt is >= 0 and <= 90 && double.IsFinite(roof.Azimuth) && roof.Azimuth is >= 0 and <= 360).ToArray();
        var maximum = valid.Length == 0 ? 1 : valid.Max(roof => roof.Capacity);
        return valid.Select((roof, index) => new SolarDiagramRoof(roof.Number, roof.Capacity, roof.Tilt, roof.Azimuth % 360,
            24 + 18 * Math.Sqrt(roof.Capacity / maximum), 12 + 24 * Math.Cos(roof.Tilt * Rad), valid.Length == 1 ? Center : index == 0 ? 132 : 188)).ToArray();
    }

    public static string Path(IReadOnlyList<SolarDiagramPoint> points) => string.Join(" ", points.Select((point, index) => $"{(index == 0 ? "M" : "L")}{F(point.X)} {F(point.Y)}"));
    public static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

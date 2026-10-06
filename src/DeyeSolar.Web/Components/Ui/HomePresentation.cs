using DeyeSolar.Web.Redesign;

namespace DeyeSolar.Web.Components.Ui;

public static class HomePresentation
{
    public static DateTimeOffset? NightSunrise(ProductionViewDto? data, DateTimeOffset now)
    {
        if (data is null || !TimeZoneInfo.TryFindSystemTimeZoneById(data.TimeZoneId, out var zone)
            || data.Date != DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime)) return null;
        if (data.Sunrise is { } sunrise && now < sunrise) return sunrise;
        return data.Sunset is { } sunset && now >= sunset
            && data.NextSunrise is { } next && next > now ? next : null;
    }

    // LiveReading has already checked quality, measurement age and the selected source.
    public static bool RunsOnBattery(LiveReading data) => data.SolarKw is >= 0 and < .05
        && data.BatteryKw is > 0 && data.GridKw is <= 0 && data.LoadKw is > 0;
}

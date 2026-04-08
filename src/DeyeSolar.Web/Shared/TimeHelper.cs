namespace DeyeSolar.Web.Shared;

public static class TimeHelper
{
    public static DateTime ToUserTime(DateTime utcTime, string? timeZoneId)
    {
        if (string.IsNullOrEmpty(timeZoneId) || timeZoneId == "UTC")
            return utcTime;

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utcTime, DateTimeKind.Utc), tz);
        }
        catch
        {
            return utcTime;
        }
    }
}

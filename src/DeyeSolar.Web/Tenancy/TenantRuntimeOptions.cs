using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;

namespace DeyeSolar.Web.Tenancy;

public static class TenantRuntimeOptions
{
    public const string ConfigureSiteMessage = "Configure your solar installation in Settings to estimate generation.";
    public static Dictionary<string, string?> Defaults(DateTimeOffset now)
    {
        var result = SettingsSchema.RuntimeDefaults();
        // Explicitly blank these even if an options class gains a deployment-specific default in future.
        // These are installation-specific assumptions, not safe defaults for somebody else's equipment.
        foreach (var key in new[] { "Latitude", "Longitude", "Roof1Kwp", "Roof2Kwp", "Roof1Tilt", "Roof2Tilt", "Roof1Azimuth", "Roof2Azimuth" })
            result[$"SolarEstimate:{key}"] = "0";
        result["SolarEstimate:LocationLabel"] = "";
        result["SolarEstimate:ApiKey"] = "";
        result["SolarEstimate:DeyeConfirmedDeviceSn"] = "";
        result["SolarEstimate:DeyeSolarPowerIsPvDcConfirmed"] = "False";
        result["SolarEstimate:OperatingModeNote"] = "";
        result["SolarEstimate:TimeZoneId"] = "UTC";
        result["SolarSales:TimeZoneId"] = "UTC";
        result["SolarSales:ContractStartDate"] = DateOnly.FromDateTime(now.UtcDateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        result["Display:TimeZoneId"] = "UTC";
        return result;
    }

    internal static bool KnownSetting(string section, string key) => SettingsSchema.IsRuntimeSetting(section, key);

    public static bool HasSolarConfiguration(SolarEstimateOptions options)
    {
        if (options.Roof1Kwp + options.Roof2Kwp <= 0) return false;
        try { options.Validate(); return true; }
        catch (ArgumentException) { return false; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}

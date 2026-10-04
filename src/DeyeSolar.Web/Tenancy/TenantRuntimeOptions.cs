using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;

namespace DeyeSolar.Web.Tenancy;

public static class TenantRuntimeOptions
{
    public const string ConfigureSiteMessage = "Configure your solar installation in Settings to estimate generation.";
    private static readonly (string Section, Type Type)[] Sections =
    [
        (DeyeCloudOptions.Section, typeof(DeyeCloudOptions)), (ShellyOptions.Section, typeof(ShellyOptions)),
        (PollingOptions.Section, typeof(PollingOptions)), ("Display", typeof(DisplayOptions)),
        (SolarEstimateOptions.Section, typeof(SolarEstimateOptions)), (SolarSalesOptions.Section, typeof(SolarSalesOptions))
    ];

    private static Dictionary<string, string?> TypedDefaults()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (section, type) in Sections)
        {
            var value = Activator.CreateInstance(type)!;
            foreach (var property in type.GetProperties().Where(property => property.CanRead && property.CanWrite))
                result[$"{section}:{property.Name}"] = AppSettingsService.ToSettingValue(property.GetValue(value));
        }
        return result;
    }

    public static Dictionary<string, string?> Defaults(DateTimeOffset now)
    {
        var result = TypedDefaults();
        // Explicitly blank these even if an options class gains a deployment-specific default in future.
        result["DeyeCloud:BaseUrl"] = "https://eu1-developer.deyecloud.com/v1.0";
        foreach (var key in new[] { "AppId", "AppSecret", "Email", "Password", "DeviceSn" }) result[$"DeyeCloud:{key}"] = "";
        result["DeyeCloud:StationId"] = "0";
        foreach (var key in new[] { "ServerUri", "AuthKey", "DeviceId" }) result[$"Shelly:{key}"] = "";
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

    internal static Dictionary<string, string?> ForInstallation(string id, DateTimeOffset now,
        IConfiguration deployment, string? legacySolarApiKey)
    {
        var defaults = id == InstallationIds.Legacy ? TypedDefaults() : Defaults(now);
        if (id == InstallationIds.Legacy)
        {
            foreach (var key in defaults.Keys.ToArray())
                if (key != "SolarEstimate:ApiKey" && deployment[key] is { } value) defaults[key] = value;
            // Capture this value before adding SQL configuration. SQL/client settings never select a server API key.
            defaults["SolarEstimate:ApiKey"] = legacySolarApiKey ?? "";
        }
        return defaults;
    }

    internal static bool KnownSetting(string section, string key) => Sections.Any(pair => pair.Section == section
        && pair.Type.GetProperties().Any(property => property.CanWrite && property.Name == key))
        && !(section == SolarEstimateOptions.Section && key == nameof(SolarEstimateOptions.ApiKey));

    public static bool HasSolarConfiguration(SolarEstimateOptions options)
    {
        if (options.Roof1Kwp + options.Roof2Kwp <= 0) return false;
        try { options.Validate(); return true; }
        catch (ArgumentException) { return false; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}

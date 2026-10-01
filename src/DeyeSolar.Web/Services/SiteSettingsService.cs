using System.Globalization;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;

namespace DeyeSolar.Web.Services;

public sealed record SolarSiteSettings(double Latitude, double Longitude, string LocationLabel, string TimeZoneId,
    double Roof1Kwp, double Roof2Kwp, double Roof1Tilt, double Roof2Tilt, double Roof1Azimuth, double Roof2Azimuth,
    bool DeyeSolarPowerIsPvDcConfirmed = false, string DeyeSolarPowerConfirmedDeviceSn = "");
public sealed record SalesSiteSettings(string ContractStartDate, string TimeZoneId, bool PayNegativePrices);
public sealed record SiteSettingsDto(SolarSiteSettings SolarEstimate, SalesSiteSettings SolarSales, string SelectedDeviceSn = "");

public sealed class SiteSettingsService(AppSettingsService settings)
{
    public async Task<SiteSettingsDto> LoadAsync()
    {
        var solar = await settings.LoadSectionAsync<SolarEstimateOptions>(SolarEstimateOptions.Section);
        var sales = await settings.LoadSectionAsync<SolarSalesOptions>(SolarSalesOptions.Section);
        var deye = await settings.LoadSectionAsync<DeyeCloudOptions>(DeyeCloudOptions.Section);
        var selectedSn = deye.DeviceSn?.Trim() ?? "";
        var confirmed = solar.DeyeSolarPowerIsPvDcConfirmed && selectedSn.Length > 0
            && solar.DeyeConfirmedDeviceSn == selectedSn;
        return new(new(solar.Latitude, solar.Longitude, solar.LocationLabel, solar.TimeZoneId,
            solar.Roof1Kwp, solar.Roof2Kwp, solar.Roof1Tilt, solar.Roof2Tilt, solar.Roof1Azimuth, solar.Roof2Azimuth,
            confirmed, confirmed ? selectedSn : ""),
            new(sales.ContractStartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), sales.TimeZoneId, sales.PayNegativePrices), selectedSn);
    }
    public static bool TryValidate(SiteSettingsDto? draft, out string message)
    {
        message = "Enter a valid location, solar capacity, roof orientation and time zone.";
        if (draft?.SolarEstimate is not { } solar || draft.SolarSales is not { } sales
            || solar.LocationLabel is null || solar.LocationLabel.Length > 120 || solar.LocationLabel.Any(char.IsControl)
            || solar.DeyeSolarPowerConfirmedDeviceSn is null || solar.DeyeSolarPowerConfirmedDeviceSn.Length > 128
            || solar.DeyeSolarPowerConfirmedDeviceSn.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(solar.TimeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(solar.TimeZoneId, out _)
            || string.IsNullOrWhiteSpace(sales.TimeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(sales.TimeZoneId, out _)) return false;
        double[] values = [solar.Latitude, solar.Longitude, solar.Roof1Kwp, solar.Roof2Kwp,
            solar.Roof1Tilt, solar.Roof2Tilt, solar.Roof1Azimuth, solar.Roof2Azimuth];
        if (values.Any(value => !double.IsFinite(value)) || solar.Latitude is < -90 or > 90 || solar.Longitude is < -180 or > 180
            || solar.Roof1Kwp < 0 || solar.Roof2Kwp < 0 || solar.Roof1Kwp + solar.Roof2Kwp is <= 0 or > 10000
            || solar.Roof1Tilt is < 0 or > 90 || solar.Roof2Tilt is < 0 or > 90
            || solar.Roof1Azimuth is < 0 or >= 360 || solar.Roof2Azimuth is < 0 or >= 360) return false;
        if (!DateOnly.TryParseExact(sales.ContractStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) || date.Year < 2000)
        { message = "Enter a contract start date from 2000 onwards as YYYY-MM-DD."; return false; }
        message = "";
        return true;
    }
    public async Task SaveAsync(SiteSettingsDto draft)
    {
        if (!TryValidate(draft, out var error)) throw new ArgumentException(error);
        var deye = await settings.LoadSectionAsync<DeyeCloudOptions>(DeyeCloudOptions.Section);
        var selectedSn = deye.DeviceSn?.Trim() ?? "";
        var solar = draft.SolarEstimate;
        if (solar.DeyeSolarPowerIsPvDcConfirmed && (selectedSn.Length == 0 || solar.DeyeSolarPowerConfirmedDeviceSn != selectedSn))
            throw new ArgumentException("Save and select the inverter in DeyeCloud settings, then reload before confirming its PV readings.");
        // Save only editable properties; advanced model assumptions and server keys remain in place.
        await settings.SaveSectionAsync(SolarEstimateOptions.Section, new
        {
            solar.Latitude, solar.Longitude, LocationLabel = solar.LocationLabel.Trim(), solar.TimeZoneId,
            solar.Roof1Kwp, solar.Roof2Kwp, solar.Roof1Tilt, solar.Roof2Tilt, solar.Roof1Azimuth, solar.Roof2Azimuth,
            solar.DeyeSolarPowerIsPvDcConfirmed, DeyeConfirmedDeviceSn = solar.DeyeSolarPowerIsPvDcConfirmed ? selectedSn : ""
        });
        await settings.SaveSectionAsync(SolarSalesOptions.Section, draft.SolarSales);
    }
}

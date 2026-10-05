using System.Globalization;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;
using DeyeSolar.Domain.Interfaces;
using Microsoft.Extensions.Options;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Services;

public sealed record SolarSiteSettings(double Latitude, double Longitude, string LocationLabel, string TimeZoneId,
    double Roof1Kwp, double Roof2Kwp, double Roof1Tilt, double Roof2Tilt, double Roof1Azimuth, double Roof2Azimuth,
    bool DeyeSolarPowerIsPvDcConfirmed = false, string DeyeSolarPowerConfirmedDeviceSn = "");
public sealed record SalesSiteSettings(string ContractStartDate, string TimeZoneId, bool PayNegativePrices);
public sealed record SiteSettingsDto(SolarSiteSettings SolarEstimate, SalesSiteSettings SolarSales, string SelectedDeviceSn = "");

public sealed class SiteSettingsService(IAppSettingsReader settings, IAppSettingsWriter writer, IOptionsMonitor<InverterConnectionOptions> inverter,
    IInverterDataSource source)
{
    private async Task<string> SelectedDeviceAsync()
    {
        if (source is IInverterSelectionRefresher refresher) await refresher.RefreshSelectionAsync(CancellationToken.None);
        return inverter.CurrentValue.DeviceKey;
    }
    public async Task<SiteSettingsDto> LoadAsync()
    {
        var solar = await settings.LoadSectionAsync<SolarEstimateOptions>(SolarEstimateOptions.Section);
        var sales = await settings.LoadSectionAsync<SolarSalesOptions>(SolarSalesOptions.Section);
        var selectedSn = await SelectedDeviceAsync();
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
        var geometry = new SolarSiteGeometry(solar.Latitude, solar.Longitude, solar.Roof1Kwp, solar.Roof2Kwp,
            solar.Roof1Tilt, solar.Roof2Tilt, solar.Roof1Azimuth, solar.Roof2Azimuth);
        if (!geometry.IsValid || geometry.TotalKwp > 10000) return false;
        if (!DateOnly.TryParseExact(sales.ContractStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) || date.Year < 2000)
        { message = "Enter a contract start date from 2000 onwards as YYYY-MM-DD."; return false; }
        message = "";
        return true;
    }
    public async Task SaveAsync(SiteSettingsDto draft)
    {
        if (!TryValidate(draft, out var error)) throw new ArgumentException(error);
        var selectedSn = await SelectedDeviceAsync();
        var solar = draft.SolarEstimate;
        if (solar.DeyeSolarPowerIsPvDcConfirmed && (selectedSn.Length == 0 || solar.DeyeSolarPowerConfirmedDeviceSn != selectedSn))
            throw new ArgumentException("Save and select the primary inverter in Integrations, then reload before confirming its PV readings.");
        // Save only editable properties; advanced model assumptions and server keys remain in place.
        await writer.SaveSectionsAsync(new Dictionary<string, object>
        {
            [SolarEstimateOptions.Section] = new
        {
            solar.Latitude, solar.Longitude, LocationLabel = solar.LocationLabel.Trim(), solar.TimeZoneId,
            solar.Roof1Kwp, solar.Roof2Kwp, solar.Roof1Tilt, solar.Roof2Tilt, solar.Roof1Azimuth, solar.Roof2Azimuth,
            solar.DeyeSolarPowerIsPvDcConfirmed, DeyeConfirmedDeviceSn = solar.DeyeSolarPowerIsPvDcConfirmed ? selectedSn : ""
            },
            [SolarSalesOptions.Section] = draft.SolarSales
        });
    }
}

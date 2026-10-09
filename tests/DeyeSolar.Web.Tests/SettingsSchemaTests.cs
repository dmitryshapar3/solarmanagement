using System.Globalization;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Tenancy;

namespace DeyeSolar.Web.Tests;

public sealed class SettingsSchemaTests
{
    [Theory]
    [InlineData("pl-PL")]
    [InlineData("de-DE")]
    public void StoredValuesRoundTripIndependentlyOfTheRequestCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var timestamp = new DateTimeOffset(2026, 10, 5, 12, 34, 56, TimeSpan.FromHours(2));
            object[] values = [5.125, 0.123456m, 0m, new DateOnly(2026, 10, 5), new TimeOnly(12, 34, 56), timestamp, true];
            foreach (var value in values)
                Assert.Equal(value, InvariantSettingCodec.Parse(InvariantSettingCodec.Format(value), value.GetType()));
            Assert.Equal("5.125", InvariantSettingCodec.Format(5.125));
            Assert.Equal("0.123456", InvariantSettingCodec.Format(0.123456m));
            Assert.Null(InvariantSettingCodec.Parse("", typeof(double?)));
            Assert.Equal("", InvariantSettingCodec.Parse("", typeof(string)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void PartialAndFullSettingsShareThePersistenceContractWithoutServerKeysOrComputedAliases()
    {
        var value = new { Roof1Kwp = 5.0, ApiKey = "server-only", TotalKwp = 5.0, SolarPowerIsPvDcConfirmed = true };
        Assert.Equal([nameof(SolarEstimateOptions.Roof1Kwp)], SettingsSchema.Properties(SolarEstimateOptions.Section, value.GetType()).Select(property => property.Name));
        var names = SettingsSchema.Properties(SolarEstimateOptions.Section, typeof(SolarEstimateOptions)).Select(property => property.Name).ToArray();
        Assert.DoesNotContain(nameof(SolarEstimateOptions.ApiKey), names);
        Assert.DoesNotContain(nameof(SolarEstimateOptions.TotalKwp), names);
        Assert.DoesNotContain(nameof(SolarEstimateOptions.SolarPowerIsPvDcConfirmed), names);
        var fingerprint = SettingsSchema.ConfigurationValues(SolarEstimateOptions.Section, new SolarEstimateOptions { ApiKey = "operator-only" });
        Assert.DoesNotContain(nameof(SolarEstimateOptions.ApiKey), fingerprint.Keys);
        Assert.Contains(nameof(SolarEstimateOptions.TotalKwp), fingerprint.Keys);
        Assert.Contains(nameof(SolarEstimateOptions.SolarPowerIsPvDcConfirmed), fingerprint.Keys);
        var defaults = TenantRuntimeOptions.Defaults(DateTimeOffset.UtcNow);
        defaults["SolarEstimate:ApiKey"] = "operator-weather-key";
        Assert.DoesNotContain(SettingsSchema.RuntimeEntries(defaults), entry => entry.Key == nameof(SolarEstimateOptions.ApiKey));
        Assert.Contains(SettingsSchema.RuntimeEntries(defaults), entry => entry.Section == SolarEstimateOptions.Section && entry.Key == nameof(SolarEstimateOptions.Roof1Kwp));
    }

    [Fact]
    public void InvalidPersistedValueKeepsItsBoundDefaultAndLabelSectionsRemainSupported()
    {
        var options = new SolarEstimateOptions { Roof1Kwp = 5.125 };
        SettingsSchema.Properties(SolarEstimateOptions.Section, typeof(SolarEstimateOptions))
            .Single(property => property.Name == nameof(SolarEstimateOptions.Roof1Kwp)).Apply(options, "5,125invalid");
        Assert.Equal(5.125, options.Roof1Kwp);
        var labels = new DeyeSolar.Web.Services.AppSettingsDeviceLabelStore.DeviceLabelsOptions { LabelsJson = "{}" };
        Assert.Equal("{}", Assert.Single(SettingsSchema.Properties("DeviceLabels", labels.GetType())).Read(labels));
    }
}

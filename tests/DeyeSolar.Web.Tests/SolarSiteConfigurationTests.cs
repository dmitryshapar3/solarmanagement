using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using Microsoft.Extensions.Configuration;

namespace DeyeSolar.Web.Tests;

public class SolarSiteConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SiteDefaultsUseEightGardenAndSevenRoadPanelsAtTwentyFiveDegrees(bool loadAppSettings)
    {
        var options = new SolarEstimateOptions();
        if (loadAppSettings)
            AppSettings().Build().GetSection(SolarEstimateOptions.Section).Bind(options);

        options.Validate();
        Assert.Equal(4.32, options.Roof1Kwp);
        Assert.Equal(3.78, options.Roof2Kwp);
        Assert.Equal(25, options.Roof1Tilt);
        Assert.Equal(25, options.Roof2Tilt);
        Assert.Equal(8.1, options.TotalKwp, 10);
        Assert.Equal(230, options.Roof1Azimuth);
        Assert.Equal(50, options.Roof2Azimuth);
        Assert.Equal(50.095278, options.Latitude);
        Assert.Equal(20.070278, options.Longitude);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedModuleDefaultsKeepTheOtherEngineeringUncertainties(bool loadAppSettings)
    {
        var options = new SolarEstimateOptions();
        if (loadAppSettings)
            AppSettings().Build().GetSection(SolarEstimateOptions.Section).Bind(options);

        options.Validate();

        Assert.Equal(-0.0026, options.TemperatureCoefficient);
        Assert.Equal(0, options.TemperatureCoefficientUncertainty);
        Assert.Equal(0.25, options.ModelUncertaintyFraction);
        Assert.Equal(0.08, options.ConfigurationUncertaintyFraction);
        Assert.Equal(12, options.CellTemperatureUncertaintyC);
        Assert.Equal(0.03, options.MinimumDcLossFraction);
        Assert.Equal(0.06, options.DcLossFraction);
        Assert.Equal(0.12, options.MaximumDcLossFraction);
    }

    [Fact]
    public void ExplicitModuleConfigurationOverridesTheNewDefaults()
    {
        var configuration = AppSettings().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SolarEstimate:TemperatureCoefficient"] = "-0.004",
            ["SolarEstimate:TemperatureCoefficientUncertainty"] = "0.001"
        }).Build();
        var options = new SolarEstimateOptions();

        configuration.GetSection(SolarEstimateOptions.Section).Bind(options);
        options.Validate();

        Assert.Equal(-0.004, options.TemperatureCoefficient);
        Assert.Equal(0.001, options.TemperatureCoefficientUncertainty);
        Assert.Equal(4.32, options.Roof1Kwp);
        Assert.Equal(3.78, options.Roof2Kwp);
        Assert.Equal(25, options.Roof1Tilt);
        Assert.Equal(25, options.Roof2Tilt);
        Assert.Equal(230, options.Roof1Azimuth);
        Assert.Equal(50, options.Roof2Azimuth);
    }

    [Theory]
    [InlineData(1000, 0, 4.32, 0, 4.32)]
    [InlineData(0, 1000, 0, 3.78, 3.78)]
    [InlineData(800, 400, 3.456, 1.512, 4.968)]
    public void RoofContributionsFollowCorrectedPanelCounts(double westGti, double eastGti,
        double westKw, double eastKw, double totalKw)
    {
        var time = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var options = new SolarEstimateOptions { TemperatureCoefficient = 0, DcLossFraction = 0, MinimumDcLossFraction = 0 };
        var observation = new SolarRadiationObservation(time, westGti, eastGti, 20, 2, time, 0);
        var estimate = SolarPowerCalculator.Calculate(observation, options, time);

        Assert.Equal(westKw, estimate.Roofs[0].CentralKw, 10);
        Assert.Equal(eastKw, estimate.Roofs[1].CentralKw, 10);
        Assert.Equal(totalKw, estimate.CentralKw, 10);
        Assert.Equal(25, estimate.Roofs[0].Tilt);
        Assert.Equal(25, estimate.Roofs[1].Tilt);
    }

    private static IConfigurationBuilder AppSettings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DeyeSolar.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return new ConfigurationBuilder().AddJsonFile(Path.Combine(directory!.FullName,
            "src", "DeyeSolar.Web", "appsettings.json"));
    }
}

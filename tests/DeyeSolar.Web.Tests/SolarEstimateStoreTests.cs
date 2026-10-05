using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

public class SolarEstimateStoreTests
{
    private static readonly DateTime Observed = new(2026, 9, 18, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SwitchingDeviceExcludesThePreviousInverterAndUnattributedHistory()
    {
        var rows = new[]
        {
            Reading(1, "previous-device", Observed, 7000),
            Reading(2, "current-device", Observed.AddSeconds(60), 3500),
            Reading(3, null, Observed, 6000),
            Reading(4, "current-device", null, 6500)
        }.AsQueryable();

        var current = SolarEstimateStore.EligibleReadings(rows, "current-device",
            Observed.AddMinutes(-2), Observed.AddMinutes(2)).ToList();
        var previous = SolarEstimateStore.EligibleReadings(rows, "previous-device",
            Observed.AddMinutes(-2), Observed.AddMinutes(2)).ToList();

        Assert.Equal(2, Assert.Single(current).Id);
        Assert.Equal(1, Assert.Single(previous).Id);
    }

    [Fact]
    public void DeviceHistoryStillRequiresMatchingTimeAndValidPower()
    {
        var rows = new[]
        {
            Reading(1, "device", Observed.AddSeconds(-121), 4000),
            Reading(2, "device", Observed.AddSeconds(121), 4000),
            Reading(3, "device", Observed, -1),
            Reading(4, "device", Observed, 0),
            Reading(5, "device", Observed, 0, solarValid: false),
            Reading(6, "device", Observed, 4000, solarValid: false)
        }.AsQueryable();

        var result = SolarEstimateStore.EligibleReadings(rows, "device",
            Observed.AddSeconds(-120), Observed.AddSeconds(120)).ToList();

        Assert.Equal(4, Assert.Single(result).Id);
    }

    [Fact]
    public void DeviceProvenanceIsNullableBoundedAndFilteredInTheDatabase()
    {
        using var db = new DeyeSolarDbContext(new DbContextOptionsBuilder<DeyeSolarDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true;TrustServerCertificate=true").Options, TestInstallation.Id);
        var property = db.Model.FindEntityType(typeof(Reading))!.FindProperty(nameof(DeyeSolar.Web.Data.Reading.SolarDeviceSn))!;
        Assert.True(property.IsNullable);
        Assert.Equal(128, property.GetMaxLength());

        var sql = SolarEstimateStore.EligibleReadings(db.Readings.AsNoTracking(), "current-device",
            Observed.AddMinutes(-2), Observed.AddMinutes(2)).ToQueryString();
        Assert.Contains("[SolarDeviceSn] =", sql);
        Assert.Contains("[SolarPowerValid] =", sql);
        Assert.Contains("[SolarObservedAt] IS NOT NULL", sql);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static Reading Reading(int id, string? deviceSn, DateTime? observed, int watts, bool solarValid = true) => new()
    {
        Id = id, SolarDeviceSn = deviceSn, SolarObservedAt = observed,
        Timestamp = Observed, SolarProduction = watts, SolarPowerValid = solarValid, DataSource = "DeyeCloud"
    };
}

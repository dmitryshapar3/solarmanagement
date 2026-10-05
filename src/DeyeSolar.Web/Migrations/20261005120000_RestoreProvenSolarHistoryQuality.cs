using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations;

/// <summary>Retains the PV measurement provenance recorded before per-metric quality flags existed.</summary>
public partial class RestoreProvenSolarHistoryQuality : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Older adapters recorded a measured PV time/device only after validating that
        // measurement. Numeric battery/load/grid columns have no equivalent proof.
        // The first explicitly written metric flag fences the old archive: never
        // reinterpret an invalid measurement recorded by the current quality contract.
        migrationBuilder.Sql("""
            DECLARE @qualityStartId int = (
                SELECT MIN([Id]) FROM [Readings]
                WHERE [BatteryPowerValid] = 1 OR [BatteryTemperatureValid] = 1
                    OR [BatteryVoltageValid] = 1 OR [BatteryCurrentValid] = 1
                    OR [LoadPowerValid] = 1 OR [GridPowerValid] = 1 OR [SolarPowerValid] = 1
            );
            UPDATE [Readings] SET [SolarPowerValid] = 1
            WHERE @qualityStartId IS NOT NULL AND [Id] < @qualityStartId
                AND [SolarPowerValid] = 0
                AND [DataSource] IN (N'Integration', N'DeyeCloud')
                AND [SolarObservedAt] >= CONVERT(datetime2, '2000-01-01T00:00:00', 126)
                AND [SolarObservedAt] <= [Timestamp]
                AND [SolarDeviceSn] IS NOT NULL AND LEN(LTRIM(RTRIM([SolarDeviceSn]))) > 0
                AND [SolarProduction] >= 0;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("THROW 51000, 'Restored PV measurement quality cannot be rolled back without losing recorded provenance. Use a forward fix or restore a verified backup.', 1;");
    }
}

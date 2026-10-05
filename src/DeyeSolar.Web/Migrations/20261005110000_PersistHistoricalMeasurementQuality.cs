using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations;

/// <summary>Persists measurement quality and preserves missing rule-run measurements.</summary>
public partial class PersistHistoricalMeasurementQuality : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var column in new[] { "BatteryPowerValid", "BatteryTemperatureValid", "BatteryVoltageValid",
            "BatteryCurrentValid", "LoadPowerValid", "GridPowerValid", "SolarPowerValid" })
            migrationBuilder.AddColumn<bool>(name: column, table: "Readings", type: "bit", nullable: false, defaultValue: false);
        foreach (var column in new[] { "BatterySoc", "SolarProduction", "BatteryPower" })
            migrationBuilder.AlterColumn<int>(name: column, table: "RuleRunLogs", type: "int", nullable: true,
                oldClrType: typeof(int), oldType: "int");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("THROW 51000, 'Historical measurement quality cannot be rolled back without losing known absence of data. Restore a verified backup for recovery.', 1;");
    }
}

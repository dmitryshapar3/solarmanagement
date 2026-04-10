using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class RebuildSolarRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop fields that no longer exist in the new drain-based design
            migrationBuilder.DropColumn(
                name: "MaxConsumptionWh",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "MonitoringWindowMinutes",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "LastTurnedOff",
                table: "TriggerRules");

            // SocTurnOnThreshold becomes required (was nullable). Backfill NULLs first.
            migrationBuilder.Sql("UPDATE TriggerRules SET SocTurnOnThreshold = 80 WHERE SocTurnOnThreshold IS NULL");
            migrationBuilder.AlterColumn<int>(
                name: "SocTurnOnThreshold",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 80,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            // New fields for drain-based rule engine
            migrationBuilder.AddColumn<int>(
                name: "SocFloor",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 55);

            migrationBuilder.AddColumn<int>(
                name: "MaxDrainWh",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 200);

            migrationBuilder.AddColumn<int>(
                name: "DrainWindowMinutes",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<int>(
                name: "MinOnMinutes",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 10);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SocFloor",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "MaxDrainWh",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "DrainWindowMinutes",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "MinOnMinutes",
                table: "TriggerRules");

            migrationBuilder.AlterColumn<int>(
                name: "SocTurnOnThreshold",
                table: "TriggerRules",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 80);

            migrationBuilder.AddColumn<int>(
                name: "MaxConsumptionWh",
                table: "TriggerRules",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MonitoringWindowMinutes",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<System.DateTime>(
                name: "LastTurnedOff",
                table: "TriggerRules",
                type: "datetime2",
                nullable: true);
        }
    }
}

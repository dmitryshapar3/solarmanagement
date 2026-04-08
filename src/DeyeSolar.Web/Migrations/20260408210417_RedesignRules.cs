using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class RedesignRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DischargeSustainedMinutes",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "MinSolarPowerWatts",
                table: "TriggerRules");

            migrationBuilder.RenameColumn(
                name: "SolarSustainedMinutes",
                table: "TriggerRules",
                newName: "MaxConsumptionWh");

            migrationBuilder.AddColumn<int>(
                name: "CooldownMinutes",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastTurnedOff",
                table: "TriggerRules",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MonitoringWindowMinutes",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CooldownMinutes",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "LastTurnedOff",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "MonitoringWindowMinutes",
                table: "TriggerRules");

            migrationBuilder.RenameColumn(
                name: "MaxConsumptionWh",
                table: "TriggerRules",
                newName: "SolarSustainedMinutes");

            migrationBuilder.AddColumn<int>(
                name: "DischargeSustainedMinutes",
                table: "TriggerRules",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinSolarPowerWatts",
                table: "TriggerRules",
                type: "int",
                nullable: true);
        }
    }
}

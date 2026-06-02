using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyTriggerRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DrainWindowMinutes",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "MaxDrainWh",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "MaxSocDropPercent",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "MinOnMinutes",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "RequireBatteryCharging",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "SocAtDrainStart",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "SocFloor",
                table: "TriggerRules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DrainWindowMinutes",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<int>(
                name: "MaxDrainWh",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 200);

            migrationBuilder.AddColumn<int>(
                name: "MaxSocDropPercent",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "MinOnMinutes",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<bool>(
                name: "RequireBatteryCharging",
                table: "TriggerRules",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SocAtDrainStart",
                table: "TriggerRules",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SocFloor",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 55);
        }
    }
}

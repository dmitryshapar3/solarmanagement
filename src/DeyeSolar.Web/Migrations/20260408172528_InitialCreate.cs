using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Section = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Readings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BatterySoc = table.Column<int>(type: "int", nullable: false),
                    BatteryTemperature = table.Column<double>(type: "float", nullable: false),
                    BatteryVoltage = table.Column<double>(type: "float", nullable: false),
                    BatteryPower = table.Column<int>(type: "int", nullable: false),
                    BatteryCurrent = table.Column<double>(type: "float", nullable: false),
                    SolarProduction = table.Column<int>(type: "int", nullable: false),
                    GridConsumption = table.Column<int>(type: "int", nullable: false),
                    LoadPower = table.Column<int>(type: "int", nullable: false),
                    DataSource = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Readings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuleRunLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RuleName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BatterySoc = table.Column<int>(type: "int", nullable: false),
                    SolarProduction = table.Column<int>(type: "int", nullable: false),
                    BatteryPower = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleRunLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TriggerRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    SocTurnOnThreshold = table.Column<int>(type: "int", nullable: true),
                    MinSolarPowerWatts = table.Column<int>(type: "int", nullable: true),
                    SolarSustainedMinutes = table.Column<int>(type: "int", nullable: true),
                    DischargeSustainedMinutes = table.Column<int>(type: "int", nullable: true),
                    ActiveFrom = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActiveTo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    CurrentState = table.Column<bool>(type: "bit", nullable: false),
                    LastEvaluated = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_Section_Key",
                table: "AppSettings",
                columns: new[] { "Section", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Readings_Timestamp",
                table: "Readings",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_RuleRunLogs_Timestamp",
                table: "RuleRunLogs",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "Readings");

            migrationBuilder.DropTable(
                name: "RuleRunLogs");

            migrationBuilder.DropTable(
                name: "TriggerRules");
        }
    }
}

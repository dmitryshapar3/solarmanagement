using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    public partial class AddExportReadings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Legacy grid values lack independent unit/measurement provenance; do not backfill them.
            migrationBuilder.CreateTable(
                name: "ExportReadings",
                columns: table => new
                {
                    DeviceSn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false,
                        collation: "Latin1_General_100_BIN2"),
                    ObservedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GridPowerWatts = table.Column<int>(type: "int", nullable: false),
                    PolledAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_ExportReadings", x => new { x.DeviceSn, x.ObservedAt }));

            migrationBuilder.CreateTable(
                name: "ExportPrices",
                columns: table => new
                {
                    StartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PricePlnPerMwh = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    RetrievedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_ExportPrices", x => x.StartUtc));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ExportReadings");
            migrationBuilder.DropTable(name: "ExportPrices");
        }
    }
}

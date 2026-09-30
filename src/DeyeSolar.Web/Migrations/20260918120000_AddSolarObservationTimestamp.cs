using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    public partial class AddSolarObservationTimestamp : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows contain polling times, so they cannot be backfilled as measurement times.
            migrationBuilder.AddColumn<DateTime>(
                name: "SolarObservedAt",
                table: "Readings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SolarDeviceSn",
                table: "Readings",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Readings_SolarObservedAt",
                table: "Readings",
                column: "SolarObservedAt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Readings_SolarObservedAt", table: "Readings");
            migrationBuilder.DropColumn(name: "SolarObservedAt", table: "Readings");
            migrationBuilder.DropColumn(name: "SolarDeviceSn", table: "Readings");
        }
    }
}

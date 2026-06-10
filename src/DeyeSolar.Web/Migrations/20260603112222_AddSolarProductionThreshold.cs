using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSolarProductionThreshold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinAverageSolarProductionWatts",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 3000);

            migrationBuilder.AddColumn<bool>(
                name: "UseSolarProductionThreshold",
                table: "TriggerRules",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinAverageSolarProductionWatts",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "UseSolarProductionThreshold",
                table: "TriggerRules");
        }
    }
}

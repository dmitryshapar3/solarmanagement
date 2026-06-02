using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSocTurnOffThreshold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SocTurnOffThreshold",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 80);

            migrationBuilder.AddColumn<bool>(
                name: "UseSeparateSocTurnOffThreshold",
                table: "TriggerRules",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SocTurnOffThreshold",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "UseSeparateSocTurnOffThreshold",
                table: "TriggerRules");
        }
    }
}

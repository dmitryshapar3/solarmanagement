using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddRuleConsecutiveFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailures",
                table: "TriggerRules",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutiveFailures",
                table: "TriggerRules");
        }
    }
}

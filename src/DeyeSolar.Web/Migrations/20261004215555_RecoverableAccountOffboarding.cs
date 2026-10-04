using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class RecoverableAccountOffboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OffboardingUserId",
                table: "Installations",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OffboardingWasEnabled",
                table: "Installations",
                type: "bit",
                nullable: true);
            migrationBuilder.CreateIndex(name: "IX_IntegrationOAuthFlows_ExpiresAt", table: "IntegrationOAuthFlows", column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 51000, 'Recoverable account offboarding cannot be rolled back. Restore a verified backup for recovery.', 1;");
        }
    }
}

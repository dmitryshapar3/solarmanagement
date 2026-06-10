using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTuyaSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppSettings WHERE Section IN ('Tuya', 'SocketBackend');");
            migrationBuilder.Sql("UPDATE TriggerRules SET Enabled = 0 WHERE EntityId LIKE 'tuya:%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}

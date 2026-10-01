using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddInstallations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Stop before changing existing identities; resolving duplicate legacy contacts is an administrator action.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM AspNetUsers WHERE NormalizedEmail IS NOT NULL GROUP BY NormalizedEmail HAVING COUNT(*) > 1)
                    THROW 51000, 'Duplicate account email addresses must be resolved before enabling registration.', 1;
                IF EXISTS (SELECT 1 FROM AspNetUsers WHERE PhoneNumber IS NOT NULL AND PhoneNumber <> '' GROUP BY PhoneNumber HAVING COUNT(*) > 1)
                    THROW 51000, 'Duplicate account phone numbers must be resolved before enabling registration.', 1;
                """);
            migrationBuilder.DropIndex(
                name: "IX_RuleRunLogs_Timestamp",
                table: "RuleRunLogs");

            migrationBuilder.DropIndex(
                name: "IX_Readings_SolarObservedAt",
                table: "Readings");

            migrationBuilder.DropIndex(
                name: "IX_Readings_Timestamp",
                table: "Readings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ExportReadings",
                table: "ExportReadings");

            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AppSettings_Section_Key",
                table: "AppSettings");

            migrationBuilder.AddColumn<string>(
                name: "InstallationId",
                table: "TriggerRules",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "InstallationId",
                table: "RuleRunLogs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "InstallationId",
                table: "Readings",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "InstallationId",
                table: "ExportReadings",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AlterColumn<string>(
                name: "PhoneNumber",
                table: "AspNetUsers",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstallationId",
                table: "AppSettings",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ExportReadings",
                table: "ExportReadings",
                columns: new[] { "InstallationId", "DeviceSn", "ObservedAt" });

            migrationBuilder.CreateTable(
                name: "Installations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Installations", x => x.Id);
                });

            migrationBuilder.Sql("""
                INSERT INTO Installations (Id, Name, CreatedAt, IsEnabled)
                VALUES ('legacy', 'Existing solar installation', SYSUTCDATETIME(), 1);
                """);

            migrationBuilder.CreateTable(
                name: "InstallationMemberships",
                columns: table => new
                {
                    InstallationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstallationMemberships", x => new { x.UserId, x.InstallationId });
                    table.ForeignKey(
                        name: "FK_InstallationMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InstallationMemberships_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Every existing account retains the same installation it could access before this migration.
            // Accounts created after migration receive a separate installation in AccountIdentityService.
            migrationBuilder.Sql("""
                INSERT INTO InstallationMemberships (UserId, InstallationId, Role)
                SELECT Id, 'legacy', 'Owner' FROM AspNetUsers;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_TriggerRules_InstallationId",
                table: "TriggerRules",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_RuleRunLogs_InstallationId_Timestamp",
                table: "RuleRunLogs",
                columns: new[] { "InstallationId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Readings_InstallationId_SolarObservedAt",
                table: "Readings",
                columns: new[] { "InstallationId", "SolarObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Readings_InstallationId_Timestamp",
                table: "Readings",
                columns: new[] { "InstallationId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail",
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_PhoneNumber",
                table: "AspNetUsers",
                column: "PhoneNumber",
                unique: true,
                filter: "[PhoneNumber] IS NOT NULL AND [PhoneNumber] <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_InstallationId_Section_Key",
                table: "AppSettings",
                columns: new[] { "InstallationId", "Section", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstallationMemberships_InstallationId",
                table: "InstallationMemberships",
                column: "InstallationId");

            migrationBuilder.AddForeignKey(
                name: "FK_AppSettings_Installations_InstallationId",
                table: "AppSettings",
                column: "InstallationId",
                principalTable: "Installations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ExportReadings_Installations_InstallationId",
                table: "ExportReadings",
                column: "InstallationId",
                principalTable: "Installations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Readings_Installations_InstallationId",
                table: "Readings",
                column: "InstallationId",
                principalTable: "Installations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RuleRunLogs_Installations_InstallationId",
                table: "RuleRunLogs",
                column: "InstallationId",
                principalTable: "Installations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TriggerRules_Installations_InstallationId",
                table: "TriggerRules",
                column: "InstallationId",
                principalTable: "Installations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM Installations WHERE Id <> 'legacy')
                    THROW 51000, 'An installation-aware backup must be restored to roll back a database with multiple installations.', 1;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_AppSettings_Installations_InstallationId",
                table: "AppSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_ExportReadings_Installations_InstallationId",
                table: "ExportReadings");

            migrationBuilder.DropForeignKey(
                name: "FK_Readings_Installations_InstallationId",
                table: "Readings");

            migrationBuilder.DropForeignKey(
                name: "FK_RuleRunLogs_Installations_InstallationId",
                table: "RuleRunLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_TriggerRules_Installations_InstallationId",
                table: "TriggerRules");

            migrationBuilder.DropTable(
                name: "InstallationMemberships");

            migrationBuilder.DropTable(
                name: "Installations");

            migrationBuilder.DropIndex(
                name: "IX_TriggerRules_InstallationId",
                table: "TriggerRules");

            migrationBuilder.DropIndex(
                name: "IX_RuleRunLogs_InstallationId_Timestamp",
                table: "RuleRunLogs");

            migrationBuilder.DropIndex(
                name: "IX_Readings_InstallationId_SolarObservedAt",
                table: "Readings");

            migrationBuilder.DropIndex(
                name: "IX_Readings_InstallationId_Timestamp",
                table: "Readings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ExportReadings",
                table: "ExportReadings");

            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_PhoneNumber",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AppSettings_InstallationId_Section_Key",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "InstallationId",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "InstallationId",
                table: "RuleRunLogs");

            migrationBuilder.DropColumn(
                name: "InstallationId",
                table: "Readings");

            migrationBuilder.DropColumn(
                name: "InstallationId",
                table: "ExportReadings");

            migrationBuilder.DropColumn(
                name: "InstallationId",
                table: "AppSettings");

            migrationBuilder.AlterColumn<string>(
                name: "PhoneNumber",
                table: "AspNetUsers",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ExportReadings",
                table: "ExportReadings",
                columns: new[] { "DeviceSn", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RuleRunLogs_Timestamp",
                table: "RuleRunLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_Readings_SolarObservedAt",
                table: "Readings",
                column: "SolarObservedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Readings_Timestamp",
                table: "Readings",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_Section_Key",
                table: "AppSettings",
                columns: new[] { "Section", "Key" },
                unique: true);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyInstallationCompatibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationDeviceAliases");

            // Historical ownership migration used a shared default. New writes must name an installation.
            migrationBuilder.Sql("""
                DECLARE @dropDefaults nvarchar(max) = N'';
                SELECT @dropDefaults += N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name)
                    + N' DROP CONSTRAINT ' + QUOTENAME(d.name) + N';'
                FROM sys.default_constraints d
                JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
                JOIN sys.tables t ON t.object_id = d.parent_object_id
                WHERE SCHEMA_NAME(t.schema_id) = N'dbo' AND c.name = N'InstallationId'
                    AND t.name IN (N'AppSettings', N'ExportReadings', N'Readings', N'RuleRunLogs', N'TriggerRules');
                EXEC sys.sp_executesql @dropDefaults;

                DELETE FROM AppSettings WHERE Section IN (N'DeyeCloud', N'Shelly', N'IntegrationMigration');

                DELETE FROM Installations WHERE Id = N'legacy'
                    AND NOT EXISTS (SELECT 1 FROM InstallationMemberships WHERE InstallationId = N'legacy')
                    AND NOT EXISTS (SELECT 1 FROM AppSettings WHERE InstallationId = N'legacy')
                    AND NOT EXISTS (SELECT 1 FROM ExportReadings WHERE InstallationId = N'legacy')
                    AND NOT EXISTS (SELECT 1 FROM Readings WHERE InstallationId = N'legacy')
                    AND NOT EXISTS (SELECT 1 FROM RuleRunLogs WHERE InstallationId = N'legacy')
                    AND NOT EXISTS (SELECT 1 FROM TriggerRules WHERE InstallationId = N'legacy')
                    AND NOT EXISTS (SELECT 1 FROM IntegrationInstances WHERE InstallationId = N'legacy');
                """);

            migrationBuilder.AlterColumn<bool>(
                name: "BatterySocValid",
                table: "Readings",
                type: "bit",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 51000, 'Restore a coordinated backup to recover the removed device aliases and ownership defaults.', 1;");
        }
    }
}

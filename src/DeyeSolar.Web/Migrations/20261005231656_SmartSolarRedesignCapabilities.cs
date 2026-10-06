using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class SmartSolarRedesignCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PauseReason",
                table: "TriggerRules",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PausedAt",
                table: "TriggerRules",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PausedByCommandId",
                table: "TriggerRules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PausedByUserId",
                table: "TriggerRules",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AddedAt",
                table: "IntegrationDeviceBindings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorUserId",
                table: "IntegrationCommands",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Client",
                table: "IntegrationCommands",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OnRuleConflict",
                table: "IntegrationCommands",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PausedRuleIdsJson",
                table: "IntegrationCommands",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoRenewEnabled",
                table: "AppleSubscriptions",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RenewalAt",
                table: "AppleSubscriptions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Client",
                table: "AccountSessions",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                table: "AccountSessions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "AccountSessions",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "AccountSessions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "ActivityEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupId = table.Column<long>(type: "bigint", nullable: true),
                    InstallationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RuleId = table.Column<int>(type: "int", nullable: true),
                    DeviceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RuleName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConfigurationVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Client = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    State = table.Column<bool>(type: "bit", nullable: true),
                    BatterySoc = table.Column<int>(type: "int", nullable: true),
                    SolarWatts = table.Column<int>(type: "int", nullable: true),
                    Generation = table.Column<long>(type: "bigint", nullable: true),
                    ValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityEvents_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AppleIdentityCredentials",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Audience = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ProtectedRefreshToken = table.Column<string>(type: "nvarchar(max)", maxLength: 30000, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppleIdentityCredentials", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_AppleIdentityCredentials_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppleIdentityRevocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Audience = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ProtectedRefreshToken = table.Column<string>(type: "nvarchar(max)", maxLength: 30000, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppleIdentityRevocations", x => x.Id);
                });

            // Preserve every existing cookie and bearer session while assigning opaque, unique public IDs.
            migrationBuilder.Sql("UPDATE [AccountSessions] SET [SessionId] = NEWID() WHERE [SessionId] = '00000000-0000-0000-0000-000000000000';");

            migrationBuilder.CreateIndex(
                name: "IX_AccountSessions_SessionId",
                table: "AccountSessions",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_InstallationId_DeviceId_OccurredAt",
                table: "ActivityEvents",
                columns: new[] { "InstallationId", "DeviceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_InstallationId_GroupId_Id",
                table: "ActivityEvents",
                columns: new[] { "InstallationId", "GroupId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_InstallationId_OccurredAt_Id",
                table: "ActivityEvents",
                columns: new[] { "InstallationId", "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_InstallationId_RuleId_OccurredAt",
                table: "ActivityEvents",
                columns: new[] { "InstallationId", "RuleId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AppleIdentityRevocations_NextAttemptAt",
                table: "AppleIdentityRevocations",
                column: "NextAttemptAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // EF can commit each migration separately. Reject before any destructive operation:
            // a later irreversible migration must not be the first guard after these columns/tables
            // (including Apple revocations independent of account records) have already been dropped.
            migrationBuilder.Sql("THROW 51000, 'SmartSolar capabilities cannot be rolled back without losing account, Apple revocation and activity data. Restore a coordinated verified backup for recovery.', 1;");
        }
    }
}

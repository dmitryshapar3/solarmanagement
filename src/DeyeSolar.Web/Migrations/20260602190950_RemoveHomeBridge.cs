using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class RemoveHomeBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BridgeCommands");

            migrationBuilder.DropTable(
                name: "BridgeDeviceShadows");

            migrationBuilder.DropTable(
                name: "BridgeHeartbeats");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BridgeCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BridgeId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CommandType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DesiredState = table.Column<bool>(type: "bit", nullable: true),
                    DeviceId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResultMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BridgeCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BridgeDeviceShadows",
                columns: table => new
                {
                    BridgeId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CurrentPowerW = table.Column<int>(type: "int", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsOn = table.Column<bool>(type: "bit", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Online = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BridgeDeviceShadows", x => new { x.BridgeId, x.DeviceId });
                });

            migrationBuilder.CreateTable(
                name: "BridgeHeartbeats",
                columns: table => new
                {
                    BridgeId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    BridgeVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    HostName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BridgeHeartbeats", x => x.BridgeId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BridgeCommands_BridgeId_Status_RequestedAt",
                table: "BridgeCommands",
                columns: new[] { "BridgeId", "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BridgeCommands_LeaseExpiresAt",
                table: "BridgeCommands",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_BridgeDeviceShadows_LastSeenAt",
                table: "BridgeDeviceShadows",
                column: "LastSeenAt");
        }
    }
}

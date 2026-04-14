using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    [DbContext(typeof(DeyeSolarDbContext))]
    [Migration("20260414120000_AddHomeBridge")]
    public partial class AddHomeBridge : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BridgeCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BridgeId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CommandType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DesiredState = table.Column<bool>(type: "bit", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LeasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResultMessage = table.Column<string>(type: "nvarchar(max)", nullable: true)
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
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Online = table.Column<bool>(type: "bit", nullable: false),
                    IsOn = table.Column<bool>(type: "bit", nullable: false),
                    CurrentPowerW = table.Column<int>(type: "int", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
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
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BridgeVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    HostName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
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

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BridgeCommands");

            migrationBuilder.DropTable(
                name: "BridgeDeviceShadows");

            migrationBuilder.DropTable(
                name: "BridgeHeartbeats");
        }
    }
}

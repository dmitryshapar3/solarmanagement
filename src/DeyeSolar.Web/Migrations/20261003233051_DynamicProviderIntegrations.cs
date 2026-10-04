using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class DynamicProviderIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceInverterId",
                table: "TriggerRules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BatterySocValid",
                table: "Readings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ConfigurationRevision",
                table: "Readings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "InverterId",
                table: "Readings",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RuntimeGeneration",
                table: "Readings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "IntegrationInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstallationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProviderId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PackageVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PackageDigest = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DescriptorDigest = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ConfigurationVersion = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AccountIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationInstances", x => x.Id);
                    table.UniqueConstraint("AK_IntegrationInstances_InstallationId_Id", x => new { x.InstallationId, x.Id });
                    table.ForeignKey(
                        name: "FK_IntegrationInstances_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationConfigurations",
                columns: table => new
                {
                    InstallationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SecretsCiphertext = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationConfigurations", x => new { x.InstallationId, x.InstanceId, x.Revision });
                    table.ForeignKey(
                        name: "FK_IntegrationConfigurations_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntegrationConfigurations_IntegrationInstances_InstallationId_InstanceId",
                        columns: x => new { x.InstallationId, x.InstanceId },
                        principalTable: "IntegrationInstances",
                        principalColumns: new[] { "InstallationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationDeviceBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstallationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RemoteId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Channel = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AccountIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationDeviceBindings", x => x.Id);
                    table.UniqueConstraint("AK_IntegrationDeviceBindings_InstallationId_Id", x => new { x.InstallationId, x.Id });
                    table.UniqueConstraint("AK_IntegrationDeviceBindings_InstallationId_InstanceId_Id", x => new { x.InstallationId, x.InstanceId, x.Id });
                    table.ForeignKey(
                        name: "FK_IntegrationDeviceBindings_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntegrationDeviceBindings_IntegrationInstances_InstallationId_InstanceId",
                        columns: x => new { x.InstallationId, x.InstanceId },
                        principalTable: "IntegrationInstances",
                        principalColumns: new[] { "InstallationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstallationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    DesiredState = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProviderOperationId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PayloadHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationCommands", x => new { x.InstallationId, x.Id });
                    table.ForeignKey(
                        name: "FK_IntegrationCommands_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntegrationCommands_IntegrationDeviceBindings_InstallationId_InstanceId_DeviceId",
                        columns: x => new { x.InstallationId, x.InstanceId, x.DeviceId },
                        principalTable: "IntegrationDeviceBindings",
                        principalColumns: new[] { "InstallationId", "InstanceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationDeviceAliases",
                columns: table => new
                {
                    InstallationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LegacyId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationDeviceAliases", x => new { x.InstallationId, x.LegacyId });
                    table.ForeignKey(
                        name: "FK_IntegrationDeviceAliases_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntegrationDeviceAliases_IntegrationDeviceBindings_InstallationId_DeviceId",
                        columns: x => new { x.InstallationId, x.DeviceId },
                        principalTable: "IntegrationDeviceBindings",
                        principalColumns: new[] { "InstallationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCommands_InstallationId_DeviceId_CreatedAt",
                table: "IntegrationCommands",
                columns: new[] { "InstallationId", "DeviceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCommands_InstallationId_InstanceId_DeviceId",
                table: "IntegrationCommands",
                columns: new[] { "InstallationId", "InstanceId", "DeviceId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationDeviceAliases_InstallationId_DeviceId",
                table: "IntegrationDeviceAliases",
                columns: new[] { "InstallationId", "DeviceId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationDeviceBindings_InstallationId_InstanceId_Kind_RemoteId_Channel",
                table: "IntegrationDeviceBindings",
                columns: new[] { "InstallationId", "InstanceId", "Kind", "RemoteId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationDeviceBindings_InstallationId_Kind",
                table: "IntegrationDeviceBindings",
                columns: new[] { "InstallationId", "Kind" },
                unique: true,
                filter: "[IsDefault] = 1 AND [Enabled] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationCommands");

            migrationBuilder.DropTable(
                name: "IntegrationConfigurations");

            migrationBuilder.DropTable(
                name: "IntegrationDeviceAliases");

            migrationBuilder.DropTable(
                name: "IntegrationDeviceBindings");

            migrationBuilder.DropTable(
                name: "IntegrationInstances");

            migrationBuilder.DropColumn(
                name: "SourceInverterId",
                table: "TriggerRules");

            migrationBuilder.DropColumn(
                name: "BatterySocValid",
                table: "Readings");

            migrationBuilder.DropColumn(
                name: "ConfigurationRevision",
                table: "Readings");

            migrationBuilder.DropColumn(
                name: "InverterId",
                table: "Readings");

            migrationBuilder.DropColumn(
                name: "RuntimeGeneration",
                table: "Readings");
        }
    }
}

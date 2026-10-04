using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class AccountBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddedByUserId",
                table: "IntegrationDeviceBindings",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppleSubscriptions",
                columns: table => new
                {
                    OriginalTransactionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    AppAccountToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TransactionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    GracePeriodExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    InvalidatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsFreeTrial = table.Column<bool>(type: "bit", nullable: false),
                    SourceSignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObservationStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppleSubscriptions", x => x.OriginalTransactionId);
                    table.ForeignKey(
                        name: "FK_AppleSubscriptions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BillingAccounts",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    AppAccountToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TrialStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingAccounts", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_BillingAccounts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationDeviceBindings_AddedByUserId_Kind",
                table: "IntegrationDeviceBindings",
                columns: new[] { "AddedByUserId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_AppleSubscriptions_UserId",
                table: "AppleSubscriptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingAccounts_AppAccountToken",
                table: "BillingAccounts",
                column: "AppAccountToken",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_IntegrationDeviceBindings_AspNetUsers_AddedByUserId",
                table: "IntegrationDeviceBindings",
                column: "AddedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Existing users receive the same cutover instant; existing socket bindings remain intact.
            migrationBuilder.Sql("""
                DECLARE @cutover datetimeoffset = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
                INSERT INTO [BillingAccounts] ([UserId], [AppAccountToken], [TrialStartedAt])
                SELECT [Id], NEWID(), @cutover FROM [AspNetUsers];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping live billing records resets trials and loses Apple ownership history.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [BillingAccounts]) OR EXISTS (SELECT 1 FROM [AppleSubscriptions])
                    THROW 51000, 'AccountBilling cannot be downgraded with live billing records. Restore a coordinated pre-billing backup instead.', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_IntegrationDeviceBindings_AspNetUsers_AddedByUserId",
                table: "IntegrationDeviceBindings");

            migrationBuilder.DropTable(
                name: "AppleSubscriptions");

            migrationBuilder.DropTable(
                name: "BillingAccounts");

            migrationBuilder.DropIndex(
                name: "IX_IntegrationDeviceBindings_AddedByUserId_Kind",
                table: "IntegrationDeviceBindings");

            migrationBuilder.DropColumn(
                name: "AddedByUserId",
                table: "IntegrationDeviceBindings");
        }
    }
}

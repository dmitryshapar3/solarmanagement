using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeyeSolar.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCurrentStateChangedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CurrentStateChangedAt",
                table: "TriggerRules",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentStateChangedAt",
                table: "TriggerRules");
        }
    }
}

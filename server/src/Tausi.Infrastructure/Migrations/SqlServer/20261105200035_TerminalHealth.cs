using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class TerminalHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastExitAt",
                table: "Terminals",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastExitReason",
                table: "Terminals",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastUpdateAt",
                table: "Terminals",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdateError",
                table: "Terminals",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdateFrom",
                table: "Terminals",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdateResult",
                table: "Terminals",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdateTo",
                table: "Terminals",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastExitAt",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "LastExitReason",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "LastUpdateAt",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "LastUpdateError",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "LastUpdateFrom",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "LastUpdateResult",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "LastUpdateTo",
                table: "Terminals");
        }
    }
}

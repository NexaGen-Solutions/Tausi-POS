using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class TerminalsAndPrinters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing terminals and printers stay in use, and keep cutting paper.
            migrationBuilder.AddColumn<bool>(
                name: "Active",
                table: "Terminals",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "DispenseHeader",
                table: "Terminals",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocalPrintersJson",
                table: "Terminals",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAt",
                table: "Terminals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscardReason",
                table: "PrintJobs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Active",
                table: "Printers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CutAfterSlip",
                table: "Printers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPrintedAt",
                table: "Printers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OpenDrawerOnReceipt",
                table: "Printers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Active",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "DispenseHeader",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "LocalPrintersJson",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "DiscardReason",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "Active",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "CutAfterSlip",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "LastPrintedAt",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "OpenDrawerOnReceipt",
                table: "Printers");
        }
    }
}

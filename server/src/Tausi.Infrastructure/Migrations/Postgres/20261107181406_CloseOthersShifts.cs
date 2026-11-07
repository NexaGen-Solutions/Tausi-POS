using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class CloseOthersShifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CashierNoticeSeenAt",
                table: "CashierShifts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloseNote",
                table: "CashierShifts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloseReason",
                table: "CashierShifts",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClosedByUserId",
                table: "CashierShifts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CountedAlone",
                table: "CashierShifts",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CashierNoticeSeenAt",
                table: "CashierShifts");

            migrationBuilder.DropColumn(
                name: "CloseNote",
                table: "CashierShifts");

            migrationBuilder.DropColumn(
                name: "CloseReason",
                table: "CashierShifts");

            migrationBuilder.DropColumn(
                name: "ClosedByUserId",
                table: "CashierShifts");

            migrationBuilder.DropColumn(
                name: "CountedAlone",
                table: "CashierShifts");
        }
    }
}

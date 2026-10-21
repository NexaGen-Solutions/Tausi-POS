using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class OrderLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BillNumber",
                table: "Tickets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BilledRound",
                table: "Tickets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PrintRound",
                table: "Tickets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                table: "OrderLines",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelledByUserId",
                table: "OrderLines",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "OrderLines",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OptionIds",
                table: "OrderLines",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PrintRound",
                table: "OrderLines",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShortName",
                table: "OrderLines",
                type: "nvarchar(18)",
                maxLength: 18,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "OrderLines",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "ModifierOptions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Lines saved before statuses existed: printed ones are Printed (round 1), the rest New.
            migrationBuilder.Sql("""
                UPDATE [OrderLines] SET
                    [Status] = CASE WHEN [PrintedAt] IS NULL THEN N'New' ELSE N'Printed' END,
                    [PrintRound] = CASE WHEN [PrintedAt] IS NULL THEN NULL ELSE 1 END,
                    [ShortName] = LEFT([Name], 18);
                UPDATE t SET [PrintRound] = 1 FROM [Tickets] t
                WHERE EXISTS (SELECT 1 FROM [OrderLines] l WHERE l.[TicketId] = t.[Id] AND l.[PrintedAt] IS NOT NULL);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_TicketId_Status",
                table: "OrderLines",
                columns: new[] { "TicketId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrderLines_TicketId_Status",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "BillNumber",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "BilledRound",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "PrintRound",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "OptionIds",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "PrintRound",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "ShortName",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "ModifierOptions");
        }
    }
}

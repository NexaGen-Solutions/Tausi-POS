using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Settling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "ReceiptNumbers");

            migrationBuilder.AddColumn<long>(
                name: "ReceiptNumber",
                table: "Tickets",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RoundingAmount",
                table: "Tickets",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SettledAt",
                table: "Tickets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SettledByUserId",
                table: "Tickets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SettledTerminalId",
                table: "Tickets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkPeriodId",
                table: "JournalEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Tendered = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Change = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MpesaCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    CardReference = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    TakenByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BroughtByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TakenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RemovedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Users_BroughtByUserId",
                        column: x => x.BroughtByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Users_TakenByUserId",
                        column: x => x.TakenByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_WorkPeriods_WorkPeriodId",
                        column: x => x.WorkPeriodId,
                        principalTable: "WorkPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ReceiptNumber",
                table: "Tickets",
                column: "ReceiptNumber",
                unique: true,
                filter: "[ReceiptNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_BroughtByUserId",
                table: "Payments",
                column: "BroughtByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_MpesaCode",
                table: "Payments",
                column: "MpesaCode",
                unique: true,
                filter: "[MpesaCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TakenByUserId",
                table: "Payments",
                column: "TakenByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TerminalId",
                table: "Payments",
                column: "TerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TicketId_Status",
                table: "Payments",
                columns: new[] { "TicketId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_WorkPeriodId",
                table: "Payments",
                column: "WorkPeriodId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_ReceiptNumber",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ReceiptNumber",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "RoundingAmount",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "SettledAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "SettledByUserId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "SettledTerminalId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "WorkPeriodId",
                table: "JournalEntries");

            migrationBuilder.DropSequence(
                name: "ReceiptNumbers");
        }
    }
}

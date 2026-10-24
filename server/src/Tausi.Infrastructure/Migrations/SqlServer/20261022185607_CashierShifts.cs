using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class CashierShifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SessionsEndedAt",
                table: "WorkPeriods",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SettledShiftId",
                table: "Tickets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ShiftId",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ShiftId",
                table: "JournalEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CashierShifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashierUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpeningFloat = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    FloatCountsJson = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CountJson = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Counted = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CountedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Expected = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Difference = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SignedOffByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashierShifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashierShifts_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashierShifts_Users_CashierUserId",
                        column: x => x.CashierUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashierShifts_WorkPeriods_WorkPeriodId",
                        column: x => x.WorkPeriodId,
                        principalTable: "WorkPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DrawerMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkPeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoryName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TerminalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrawerMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrawerMovements_CashierShifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "CashierShifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ShiftId",
                table: "Payments",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_CashierShifts_CashierUserId_Status",
                table: "CashierShifts",
                columns: new[] { "CashierUserId", "Status" },
                unique: true,
                filter: "[Status] = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_CashierShifts_TerminalId_Status",
                table: "CashierShifts",
                columns: new[] { "TerminalId", "Status" },
                unique: true,
                filter: "[Status] = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_CashierShifts_WorkPeriodId",
                table: "CashierShifts",
                column: "WorkPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_DrawerMovements_ShiftId",
                table: "DrawerMovements",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseCategories_Name",
                table: "ExpenseCategories",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DrawerMovements");

            migrationBuilder.DropTable(
                name: "ExpenseCategories");

            migrationBuilder.DropTable(
                name: "CashierShifts");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ShiftId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "SessionsEndedAt",
                table: "WorkPeriods");

            migrationBuilder.DropColumn(
                name: "SettledShiftId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ShiftId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ShiftId",
                table: "JournalEntries");
        }
    }
}

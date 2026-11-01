using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class TrainingMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkPeriods_IsOpen",
                table: "WorkPeriods");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_Number",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_ReceiptNumber",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Payments_MpesaCode",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_CashierShifts_CashierUserId_Status",
                table: "CashierShifts");

            migrationBuilder.DropIndex(
                name: "IX_CashierShifts_TerminalId_Status",
                table: "CashierShifts");

            migrationBuilder.AddColumn<bool>(
                name: "IsTraining",
                table: "WorkPeriods",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTraining",
                table: "VoidLogs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTraining",
                table: "Tickets",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TrainingMode",
                table: "Terminals",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTraining",
                table: "Payments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTraining",
                table: "OrderLines",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTraining",
                table: "GiftLogs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTraining",
                table: "DrawerMovements",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTraining",
                table: "DiscountLogs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTraining",
                table: "CashierShifts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTraining",
                table: "AuditEntries",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTraining",
                table: "ApprovalRequests",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_WorkPeriods_IsOpen_IsTraining",
                table: "WorkPeriods",
                columns: new[] { "IsOpen", "IsTraining" },
                unique: true,
                filter: "[IsOpen] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Number_IsTraining",
                table: "Tickets",
                columns: new[] { "Number", "IsTraining" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ReceiptNumber_IsTraining",
                table: "Tickets",
                columns: new[] { "ReceiptNumber", "IsTraining" },
                unique: true,
                filter: "[ReceiptNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_MpesaCode_IsTraining",
                table: "Payments",
                columns: new[] { "MpesaCode", "IsTraining" },
                unique: true,
                filter: "[MpesaCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CashierShifts_CashierUserId_Status_IsTraining",
                table: "CashierShifts",
                columns: new[] { "CashierUserId", "Status", "IsTraining" },
                unique: true,
                filter: "[Status] = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_CashierShifts_TerminalId_Status_IsTraining",
                table: "CashierShifts",
                columns: new[] { "TerminalId", "Status", "IsTraining" },
                unique: true,
                filter: "[Status] = 'Open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkPeriods_IsOpen_IsTraining",
                table: "WorkPeriods");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_Number_IsTraining",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_ReceiptNumber_IsTraining",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Payments_MpesaCode_IsTraining",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_CashierShifts_CashierUserId_Status_IsTraining",
                table: "CashierShifts");

            migrationBuilder.DropIndex(
                name: "IX_CashierShifts_TerminalId_Status_IsTraining",
                table: "CashierShifts");

            migrationBuilder.DropColumn(
                name: "IsTraining",
                table: "WorkPeriods");

            migrationBuilder.DropColumn(
                name: "IsTraining",
                table: "VoidLogs");

            migrationBuilder.DropColumn(
                name: "IsTraining",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "TrainingMode",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "IsTraining",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "IsTraining",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "IsTraining",
                table: "GiftLogs");

            migrationBuilder.DropColumn(
                name: "IsTraining",
                table: "DrawerMovements");

            migrationBuilder.DropColumn(
                name: "IsTraining",
                table: "DiscountLogs");

            migrationBuilder.DropColumn(
                name: "IsTraining",
                table: "CashierShifts");

            migrationBuilder.DropColumn(
                name: "IsTraining",
                table: "AuditEntries");

            migrationBuilder.DropColumn(
                name: "IsTraining",
                table: "ApprovalRequests");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPeriods_IsOpen",
                table: "WorkPeriods",
                column: "IsOpen",
                unique: true,
                filter: "[IsOpen] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Number",
                table: "Tickets",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ReceiptNumber",
                table: "Tickets",
                column: "ReceiptNumber",
                unique: true,
                filter: "[ReceiptNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_MpesaCode",
                table: "Payments",
                column: "MpesaCode",
                unique: true,
                filter: "[MpesaCode] IS NOT NULL");

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
        }
    }
}

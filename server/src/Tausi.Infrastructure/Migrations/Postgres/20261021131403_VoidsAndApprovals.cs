using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class VoidsAndApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscountKind",
                table: "Tickets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscountReason",
                table: "Tickets",
                type: "character varying(230)",
                maxLength: 230,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountValue",
                table: "Tickets",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "PriceChangeLogs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Menu");

            migrationBuilder.AddColumn<Guid>(
                name: "OrderLineId",
                table: "PriceChangeLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "PriceChangeLogs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TicketId",
                table: "PriceChangeLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Discount",
                table: "OrderLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "DispensePrinterId",
                table: "OrderLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsGift",
                table: "OrderLines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalUnitPrice",
                table: "OrderLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidReason",
                table: "OrderLines",
                type: "character varying(230)",
                maxLength: 230,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Reason = table.Column<string>(type: "character varying(230)", maxLength: 230, nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PreferredApproverId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WorkPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalRequests_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApprovalRequests_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApprovalRequests_Users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DiscountLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OldKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    OldValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovalRequestId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GiftLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    MenuItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ItemName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovalRequestId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiftLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VoidLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    MenuItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ItemName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WasPoured = table.Column<bool>(type: "boolean", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovalRequestId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoidLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceChangeLogs_TicketId",
                table: "PriceChangeLogs",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_DispensePrinterId",
                table: "OrderLines",
                column: "DispensePrinterId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_RequestedByUserId",
                table: "ApprovalRequests",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_Status_RequestedAt",
                table: "ApprovalRequests",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_TerminalId",
                table: "ApprovalRequests",
                column: "TerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_TicketId_Status",
                table: "ApprovalRequests",
                columns: new[] { "TicketId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_WorkPeriodId",
                table: "ApprovalRequests",
                column: "WorkPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountLogs_TicketId",
                table: "DiscountLogs",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_GiftLogs_At",
                table: "GiftLogs",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_GiftLogs_TicketId",
                table: "GiftLogs",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_VoidLogs_At",
                table: "VoidLogs",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_VoidLogs_TicketId",
                table: "VoidLogs",
                column: "TicketId");

            migrationBuilder.AddForeignKey(
                name: "FK_OrderLines_Printers_DispensePrinterId",
                table: "OrderLines",
                column: "DispensePrinterId",
                principalTable: "Printers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrderLines_Printers_DispensePrinterId",
                table: "OrderLines");

            migrationBuilder.DropTable(
                name: "ApprovalRequests");

            migrationBuilder.DropTable(
                name: "DiscountLogs");

            migrationBuilder.DropTable(
                name: "GiftLogs");

            migrationBuilder.DropTable(
                name: "VoidLogs");

            migrationBuilder.DropIndex(
                name: "IX_PriceChangeLogs_TicketId",
                table: "PriceChangeLogs");

            migrationBuilder.DropIndex(
                name: "IX_OrderLines_DispensePrinterId",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "DiscountKind",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "DiscountReason",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "DiscountValue",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "PriceChangeLogs");

            migrationBuilder.DropColumn(
                name: "OrderLineId",
                table: "PriceChangeLogs");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "PriceChangeLogs");

            migrationBuilder.DropColumn(
                name: "TicketId",
                table: "PriceChangeLogs");

            migrationBuilder.DropColumn(
                name: "Discount",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "DispensePrinterId",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "IsGift",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "OriginalUnitPrice",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "VoidReason",
                table: "OrderLines");
        }
    }
}

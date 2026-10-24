using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class CashBook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "BusinessDate",
                table: "WorkPeriods",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPrivate",
                table: "StoredFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ExpenseCategoryId",
                table: "JournalLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BusinessDate",
                table: "JournalEntries",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsLate",
                table: "JournalEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversedByEntryId",
                table: "JournalEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversesEntryId",
                table: "JournalEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MoneyDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Fee = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    From = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    To = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: true),
                    Payee = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AttachmentFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeliveryIds = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    OpeningLines = table.Column<string>(type: "text", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectNote = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ReversalRequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReversalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoneyDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MoneyDocuments_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_BusinessDate",
                table: "JournalEntries",
                column: "BusinessDate");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyDocuments_BusinessDate",
                table: "MoneyDocuments",
                column: "BusinessDate");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyDocuments_CreatedByUserId",
                table: "MoneyDocuments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyDocuments_Kind_Status",
                table: "MoneyDocuments",
                columns: new[] { "Kind", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MoneyDocuments");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_BusinessDate",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "BusinessDate",
                table: "WorkPeriods");

            migrationBuilder.DropColumn(
                name: "IsPrivate",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "ExpenseCategoryId",
                table: "JournalLines");

            migrationBuilder.DropColumn(
                name: "BusinessDate",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "IsLate",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "ReversedByEntryId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "ReversesEntryId",
                table: "JournalEntries");
        }
    }
}

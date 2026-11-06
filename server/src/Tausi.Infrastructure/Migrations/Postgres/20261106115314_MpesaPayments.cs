using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class MpesaPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_MpesaCode_IsTraining",
                table: "Payments");

            migrationBuilder.AddColumn<Guid>(
                name: "MpesaTransactionId",
                table: "Payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MpesaAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumerKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ConsumerSecret = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Passkey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RelayApiKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    C2BRegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StkCallbackUrl = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    C2BRegisteredFor = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MpesaAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MpesaPrompts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CheckoutRequestId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    MerchantRequestId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    SentByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromOrderScreen = table.Column<bool>(type: "boolean", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResultCode = table.Column<int>(type: "integer", nullable: true),
                    ResultMessage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastQueriedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MpesaPrompts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MpesaPrompts_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MpesaPrompts_Users_SentByUserId",
                        column: x => x.SentByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MpesaTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AmountApplied = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PayerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ShortCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    BillReference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TypedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    MatchedTicketId = table.Column<Guid>(type: "uuid", nullable: true),
                    PromptId = table.Column<Guid>(type: "uuid", nullable: true),
                    NotFromPosReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    NotFromPosAccount = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    NotFromPosByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    NotFromPosAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MpesaTransactions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_MpesaCode_IsTraining_Untracked",
                table: "Payments",
                columns: new[] { "MpesaCode", "IsTraining" },
                unique: true,
                filter: "\"MpesaCode\" IS NOT NULL AND \"MpesaTransactionId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_MpesaTransactionId",
                table: "Payments",
                column: "MpesaTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_MpesaPrompts_CheckoutRequestId",
                table: "MpesaPrompts",
                column: "CheckoutRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MpesaPrompts_SentByUserId",
                table: "MpesaPrompts",
                column: "SentByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MpesaPrompts_TicketId_Status",
                table: "MpesaPrompts",
                columns: new[] { "TicketId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MpesaTransactions_At",
                table: "MpesaTransactions",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_MpesaTransactions_Status_ReceivedAt",
                table: "MpesaTransactions",
                columns: new[] { "Status", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MpesaTransactions_TransactionId",
                table: "MpesaTransactions",
                column: "TransactionId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_MpesaTransactions_MpesaTransactionId",
                table: "Payments",
                column: "MpesaTransactionId",
                principalTable: "MpesaTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_MpesaTransactions_MpesaTransactionId",
                table: "Payments");

            migrationBuilder.DropTable(
                name: "MpesaAccounts");

            migrationBuilder.DropTable(
                name: "MpesaPrompts");

            migrationBuilder.DropTable(
                name: "MpesaTransactions");

            migrationBuilder.DropIndex(
                name: "IX_Payments_MpesaCode_IsTraining_Untracked",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_MpesaTransactionId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "MpesaTransactionId",
                table: "Payments");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_MpesaCode_IsTraining",
                table: "Payments",
                columns: new[] { "MpesaCode", "IsTraining" },
                unique: true,
                filter: "\"MpesaCode\" IS NOT NULL");
        }
    }
}

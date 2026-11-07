using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Relay.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Businesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ApiKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApiKeyPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PathToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Businesses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    TransactionId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CheckoutRequestId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    MerchantRequestId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ResultCode = table.Column<int>(type: "integer", nullable: true),
                    ResultDesc = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PayerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ShortCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    BillRef = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    TransactionType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Businesses_ApiKeyHash",
                table: "Businesses",
                column: "ApiKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Businesses_PathToken",
                table: "Businesses",
                column: "PathToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_BusinessId_AcknowledgedAt",
                table: "Payments",
                columns: new[] { "BusinessId", "AcknowledgedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_BusinessId_Key",
                table: "Payments",
                columns: new[] { "BusinessId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "Businesses");
        }
    }
}

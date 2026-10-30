using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class PrintJobsByTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_TicketId",
                table: "PrintJobs");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_TicketId_Status_CreatedAt",
                table: "PrintJobs",
                columns: new[] { "TicketId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_TicketId_Status_CreatedAt",
                table: "PrintJobs");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_TicketId",
                table: "PrintJobs",
                column: "TicketId");
        }
    }
}

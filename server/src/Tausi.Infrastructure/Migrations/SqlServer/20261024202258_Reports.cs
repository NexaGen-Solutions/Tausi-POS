using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Reports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_WorkPeriodId",
                table: "Tickets");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "PrintJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReprint",
                table: "PrintJobs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "PrintJobs",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SlipClearedAt",
                table: "PrintJobs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkPeriods_BusinessDate",
                table: "WorkPeriods",
                column: "BusinessDate");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_WorkPeriodId_Status",
                table: "Tickets",
                columns: new[] { "WorkPeriodId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_CreatedAt",
                table: "PrintJobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_CreatedByUserId",
                table: "PrintJobs",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountLogs_At",
                table: "DiscountLogs",
                column: "At");

            migrationBuilder.AddForeignKey(
                name: "FK_PrintJobs_Users_CreatedByUserId",
                table: "PrintJobs",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PrintJobs_Users_CreatedByUserId",
                table: "PrintJobs");

            migrationBuilder.DropIndex(
                name: "IX_WorkPeriods_BusinessDate",
                table: "WorkPeriods");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_WorkPeriodId_Status",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_CreatedAt",
                table: "PrintJobs");

            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_CreatedByUserId",
                table: "PrintJobs");

            migrationBuilder.DropIndex(
                name: "IX_DiscountLogs_At",
                table: "DiscountLogs");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "IsReprint",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "SlipClearedAt",
                table: "PrintJobs");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_WorkPeriodId",
                table: "Tickets",
                column: "WorkPeriodId");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class SessionsWorkPeriodsPinLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing users' PIN lengths cannot be known from their hashes; 6 (the longest
            // allowed) only means the pad waits for Enter instead of submitting early.
            migrationBuilder.AddColumn<int>(
                name: "PinLength",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkPeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OpenedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpenedTerminalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedTerminalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsOpen = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkPeriods", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_TerminalId_EndedAt",
                table: "UserSessions",
                columns: new[] { "TerminalId", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId",
                table: "UserSessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPeriods_IsOpen",
                table: "WorkPeriods",
                column: "IsOpen",
                unique: true,
                filter: "[IsOpen] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPeriods_OpenedAt",
                table: "WorkPeriods",
                column: "OpenedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSessions");

            migrationBuilder.DropTable(
                name: "WorkPeriods");

            migrationBuilder.DropColumn(
                name: "PinLength",
                table: "Users");
        }
    }
}

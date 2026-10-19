using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexagen.Infrastructure.Migrations.Postgres
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
                type: "integer",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkPeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OpenedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenedTerminalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedTerminalId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
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
                filter: "\"IsOpen\"");

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

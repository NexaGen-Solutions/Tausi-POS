using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class LedgerCalendarDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "OldBusinessDate",
                table: "JournalEntries",
                type: "date",
                nullable: true);

            // Version 1.0.1 moves sales to calendar dates (LedgerBackfill does it at startup). Every entry made
            // before it keeps its old date here, which also tells the back-fill which entries to move.
            migrationBuilder.Sql("UPDATE \"JournalEntries\" SET \"OldBusinessDate\" = \"BusinessDate\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OldBusinessDate",
                table: "JournalEntries");
        }
    }
}

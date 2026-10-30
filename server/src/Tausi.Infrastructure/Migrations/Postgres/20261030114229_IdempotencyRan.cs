using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class IdempotencyRan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Ran",
                table: "IdempotencyRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Ran",
                table: "IdempotencyRecords");
        }
    }
}

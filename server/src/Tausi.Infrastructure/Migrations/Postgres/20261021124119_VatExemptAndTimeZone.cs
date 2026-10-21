using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class VatExemptAndTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "VatExempt",
                table: "OrderLines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "VatExempt",
                table: "MenuItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "BusinessSettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VatExempt",
                table: "OrderLines");

            migrationBuilder.DropColumn(
                name: "VatExempt",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "BusinessSettings");
        }
    }
}

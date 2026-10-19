using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexagen.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class StaffAndPrinterTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Users",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SystemKey",
                table: "Roles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DispenseHeader",
                table: "Printers",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DispenseTemplate",
                table: "Printers",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            // Existing users get their normalised name before names become unique (ignoring case).
            migrationBuilder.Sql("UPDATE [Users] SET [NormalizedName] = UPPER(LTRIM(RTRIM([Name])));");

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedName",
                table: "Users",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_SystemKey",
                table: "Roles",
                column: "SystemKey",
                unique: true,
                filter: "[SystemKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedName",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Roles_SystemKey",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SystemKey",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "DispenseHeader",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "DispenseTemplate",
                table: "Printers");
        }
    }
}

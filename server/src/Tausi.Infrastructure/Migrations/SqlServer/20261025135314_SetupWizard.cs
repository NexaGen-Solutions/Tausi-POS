using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class SetupWizard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "Users",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "Tables",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "PriceLists",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "ModifierGroups",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "MenuItems",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Key",
                table: "Licences",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500);

            migrationBuilder.AddColumn<string>(
                name: "Edition",
                table: "Licences",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsTrial",
                table: "Licences",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "IssuedAt",
                table: "Licences",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "Licences",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LicenceId",
                table: "Licences",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupportUntil",
                table: "Licences",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Actor",
                table: "AuditEntries",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SetupProgress",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Step = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SetupProgress", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SetupStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TokenHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TokenCreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SessionHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BusinessType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SetupStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_ExternalRef",
                table: "Users",
                column: "ExternalRef");

            migrationBuilder.CreateIndex(
                name: "IX_Tables_ExternalRef",
                table: "Tables",
                column: "ExternalRef");

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_ExternalRef",
                table: "PriceLists",
                column: "ExternalRef");

            migrationBuilder.CreateIndex(
                name: "IX_ModifierGroups_ExternalRef",
                table: "ModifierGroups",
                column: "ExternalRef");

            migrationBuilder.CreateIndex(
                name: "IX_MenuItems_ExternalRef",
                table: "MenuItems",
                column: "ExternalRef");

            migrationBuilder.CreateIndex(
                name: "IX_SetupProgress_Step",
                table: "SetupProgress",
                column: "Step",
                unique: true);

            // A database that already has users (every dev and demo database) is set up: no wizard for it.
            migrationBuilder.Sql("INSERT INTO [SetupStates] ([Id], [CompletedAt], [FailedAttempts]) SELECT NEWID(), SYSDATETIMEOFFSET(), 0 WHERE EXISTS (SELECT 1 FROM [Users]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SetupProgress");

            migrationBuilder.DropTable(
                name: "SetupStates");

            migrationBuilder.DropIndex(
                name: "IX_Users_ExternalRef",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Tables_ExternalRef",
                table: "Tables");

            migrationBuilder.DropIndex(
                name: "IX_PriceLists_ExternalRef",
                table: "PriceLists");

            migrationBuilder.DropIndex(
                name: "IX_ModifierGroups_ExternalRef",
                table: "ModifierGroups");

            migrationBuilder.DropIndex(
                name: "IX_MenuItems_ExternalRef",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "ExternalRef",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ExternalRef",
                table: "Tables");

            migrationBuilder.DropColumn(
                name: "ExternalRef",
                table: "PriceLists");

            migrationBuilder.DropColumn(
                name: "ExternalRef",
                table: "ModifierGroups");

            migrationBuilder.DropColumn(
                name: "ExternalRef",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "Edition",
                table: "Licences");

            migrationBuilder.DropColumn(
                name: "IsTrial",
                table: "Licences");

            migrationBuilder.DropColumn(
                name: "IssuedAt",
                table: "Licences");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "Licences");

            migrationBuilder.DropColumn(
                name: "LicenceId",
                table: "Licences");

            migrationBuilder.DropColumn(
                name: "SupportUntil",
                table: "Licences");

            migrationBuilder.DropColumn(
                name: "Actor",
                table: "AuditEntries");

            migrationBuilder.AlterColumn<string>(
                name: "Key",
                table: "Licences",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(1000)",
                oldMaxLength: 1000);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.Postgres
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
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "Tables",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "PriceLists",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "ModifierGroups",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "MenuItems",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Key",
                table: "Licences",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AddColumn<string>(
                name: "Edition",
                table: "Licences",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsTrial",
                table: "Licences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "IssuedAt",
                table: "Licences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "Licences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LicenceId",
                table: "Licences",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupportUntil",
                table: "Licences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Actor",
                table: "AuditEntries",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SetupProgress",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Step = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SetupProgress", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SetupStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TokenCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SessionHash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BusinessType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true)
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
            migrationBuilder.Sql("INSERT INTO \"SetupStates\" (\"Id\", \"CompletedAt\", \"FailedAttempts\") SELECT gen_random_uuid(), now(), 0 WHERE EXISTS (SELECT 1 FROM \"Users\");");
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
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);
        }
    }
}

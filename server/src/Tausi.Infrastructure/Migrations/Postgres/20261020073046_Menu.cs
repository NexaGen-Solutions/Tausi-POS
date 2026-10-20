using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Menu : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PriceListId",
                table: "Terminals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModifierGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    MinSelect = table.Column<int>(type: "integer", nullable: false),
                    MaxSelect = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModifierGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceChangeLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MenuItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    PriceListId = table.Column<Guid>(type: "uuid", nullable: true),
                    ModifierOptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    OldPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    NewPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TerminalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceChangeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MenuItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ShortName = table.Column<string>(type: "character varying(18)", maxLength: 18, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    VatRate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    ErpItemCode = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MenuItems_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ModifierOptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    PriceAddOn = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModifierOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModifierOptions_ModifierGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ModifierGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MenuItemPrices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MenuItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PriceListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuItemPrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MenuItemPrices_MenuItems_MenuItemId",
                        column: x => x.MenuItemId,
                        principalTable: "MenuItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MenuItemPrices_PriceLists_PriceListId",
                        column: x => x.PriceListId,
                        principalTable: "PriceLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ModifierGroupAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    MenuItemId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModifierGroupAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModifierGroupAssignments_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ModifierGroupAssignments_MenuItems_MenuItemId",
                        column: x => x.MenuItemId,
                        principalTable: "MenuItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ModifierGroupAssignments_ModifierGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ModifierGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Terminals_PriceListId",
                table: "Terminals",
                column: "PriceListId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Name",
                table: "Categories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MenuItemPrices_MenuItemId_PriceListId",
                table: "MenuItemPrices",
                columns: new[] { "MenuItemId", "PriceListId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MenuItemPrices_PriceListId",
                table: "MenuItemPrices",
                column: "PriceListId");

            migrationBuilder.CreateIndex(
                name: "IX_MenuItems_CategoryId_SortOrder",
                table: "MenuItems",
                columns: new[] { "CategoryId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_MenuItems_Name",
                table: "MenuItems",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModifierGroupAssignments_CategoryId",
                table: "ModifierGroupAssignments",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ModifierGroupAssignments_GroupId_CategoryId",
                table: "ModifierGroupAssignments",
                columns: new[] { "GroupId", "CategoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModifierGroupAssignments_GroupId_MenuItemId",
                table: "ModifierGroupAssignments",
                columns: new[] { "GroupId", "MenuItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModifierGroupAssignments_MenuItemId",
                table: "ModifierGroupAssignments",
                column: "MenuItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ModifierGroups_Name",
                table: "ModifierGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModifierOptions_GroupId_Name",
                table: "ModifierOptions",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceChangeLogs_At",
                table: "PriceChangeLogs",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_PriceChangeLogs_MenuItemId_At",
                table: "PriceChangeLogs",
                columns: new[] { "MenuItemId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_IsDefault",
                table: "PriceLists",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_Name",
                table: "PriceLists",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Terminals_PriceLists_PriceListId",
                table: "Terminals",
                column: "PriceListId",
                principalTable: "PriceLists",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Terminals_PriceLists_PriceListId",
                table: "Terminals");

            migrationBuilder.DropTable(
                name: "MenuItemPrices");

            migrationBuilder.DropTable(
                name: "ModifierGroupAssignments");

            migrationBuilder.DropTable(
                name: "ModifierOptions");

            migrationBuilder.DropTable(
                name: "PriceChangeLogs");

            migrationBuilder.DropTable(
                name: "PriceLists");

            migrationBuilder.DropTable(
                name: "MenuItems");

            migrationBuilder.DropTable(
                name: "ModifierGroups");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Terminals_PriceListId",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "PriceListId",
                table: "Terminals");
        }
    }
}

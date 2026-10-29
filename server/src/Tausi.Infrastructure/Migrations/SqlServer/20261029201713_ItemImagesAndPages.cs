using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tausi.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class ItemImagesAndPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ImageFileId",
                table: "MenuItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ItemsPerPage",
                table: "Categories",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageFileId",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "ItemsPerPage",
                table: "Categories");
        }
    }
}

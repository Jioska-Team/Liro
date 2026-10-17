using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Liro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogPriorityRefresh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ThumbnailCheckedAtUtc",
                table: "Items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CatalogPriorityRefreshes",
                columns: table => new
                {
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    ItemType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogPriorityRefreshes", x => new { x.ItemType, x.ItemId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogPriorityRefreshes_DetectedAtUtc",
                table: "CatalogPriorityRefreshes",
                column: "DetectedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogPriorityRefreshes");

            migrationBuilder.DropColumn(
                name: "ThumbnailCheckedAtUtc",
                table: "Items");
        }
    }
}

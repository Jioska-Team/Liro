using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Liro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogLimitedWatchlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogLimitedWatches",
                columns: table => new
                {
                    ItemType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextCheckAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogLimitedWatches", x => new { x.ItemType, x.ItemId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogLimitedWatches_NextCheckAtUtc_ItemType_ItemId",
                table: "CatalogLimitedWatches",
                columns: new[] { "NextCheckAtUtc", "ItemType", "ItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogLimitedWatches");
        }
    }
}

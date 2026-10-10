using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Liro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddItemMarketObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AssetTypeId",
                table: "Items",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CreatorId",
                table: "Items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatorName",
                table: "Items",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatorType",
                table: "Items",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Items",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "Restrictions",
                table: "Items",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<DateTime>(
                name: "RobloxCreatedAtUtc",
                table: "Items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RobloxUpdatedAtUtc",
                table: "Items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ItemMarketSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Data_FavoriteCount = table.Column<long>(type: "bigint", nullable: true),
                    Data_HasResellers = table.Column<bool>(type: "boolean", nullable: true),
                    Data_IsOffSale = table.Column<bool>(type: "boolean", nullable: true),
                    Data_IsResellable = table.Column<bool>(type: "boolean", nullable: true),
                    Data_LowestPriceRobux = table.Column<long>(type: "bigint", nullable: true),
                    Data_LowestResalePriceRobux = table.Column<long>(type: "bigint", nullable: true),
                    Data_PriceBeforeDiscountRobux = table.Column<long>(type: "bigint", nullable: true),
                    Data_PriceStatus = table.Column<string>(type: "text", nullable: true),
                    Data_PrimaryPriceRobux = table.Column<long>(type: "bigint", nullable: true),
                    Data_TotalQuantity = table.Column<long>(type: "bigint", nullable: true),
                    Data_UnitsAvailableForConsumption = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemMarketSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemMarketSnapshots_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ItemMarketState",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Data_FavoriteCount = table.Column<long>(type: "bigint", nullable: true),
                    Data_HasResellers = table.Column<bool>(type: "boolean", nullable: true),
                    Data_IsOffSale = table.Column<bool>(type: "boolean", nullable: true),
                    Data_IsResellable = table.Column<bool>(type: "boolean", nullable: true),
                    Data_LowestPriceRobux = table.Column<long>(type: "bigint", nullable: true),
                    Data_LowestResalePriceRobux = table.Column<long>(type: "bigint", nullable: true),
                    Data_PriceBeforeDiscountRobux = table.Column<long>(type: "bigint", nullable: true),
                    Data_PriceStatus = table.Column<string>(type: "text", nullable: true),
                    Data_PrimaryPriceRobux = table.Column<long>(type: "bigint", nullable: true),
                    Data_TotalQuantity = table.Column<long>(type: "bigint", nullable: true),
                    Data_UnitsAvailableForConsumption = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemMarketState", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_ItemMarketState_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ItemMarketSnapshots_ItemId_ObservedAtUtc",
                table: "ItemMarketSnapshots",
                columns: new[] { "ItemId", "ObservedAtUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemMarketSnapshots");

            migrationBuilder.DropTable(
                name: "ItemMarketState");

            migrationBuilder.DropColumn(
                name: "AssetTypeId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "CreatorId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "CreatorName",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "CreatorType",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "Restrictions",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "RobloxCreatedAtUtc",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "RobloxUpdatedAtUtc",
                table: "Items");
        }
    }
}

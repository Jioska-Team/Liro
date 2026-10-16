using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Liro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogItemTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Items_RobloxAssetId",
                table: "Items");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CatalogImportFailures",
                table: "CatalogImportFailures");

            migrationBuilder.AddColumn<int>(
                name: "BundleTypeId",
                table: "Items",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ItemType",
                table: "Items",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Asset");

            migrationBuilder.AddColumn<string>(
                name: "ItemType",
                table: "CatalogImportFailures",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Asset");

            migrationBuilder.AlterColumn<string>(
                name: "MarketplaceUrl",
                table: "Items",
                type: "text",
                nullable: false,
                computedColumnSql: "CASE WHEN \"ItemType\" = 'Bundle' THEN 'https://www.roblox.com/bundles/' ELSE 'https://www.roblox.com/catalog/' END || \"RobloxAssetId\"::text",
                stored: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldComputedColumnSql: "'https://www.roblox.com/catalog/' || \"RobloxAssetId\"::text",
                oldStored: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_CatalogImportFailures",
                table: "CatalogImportFailures",
                columns: new[] { "ItemType", "AssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_ItemType_RobloxAssetId",
                table: "Items",
                columns: new[] { "ItemType", "RobloxAssetId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuse to collapse typed identities if bundles have already been imported.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "Items" WHERE "ItemType" = 'Bundle')
                       OR EXISTS (SELECT 1 FROM "CatalogImportFailures" WHERE "ItemType" = 'Bundle') THEN
                        RAISE EXCEPTION 'Cannot downgrade while bundle records exist; restore a reviewed backup instead.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropIndex(
                name: "IX_Items_ItemType_RobloxAssetId",
                table: "Items");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CatalogImportFailures",
                table: "CatalogImportFailures");

            migrationBuilder.AlterColumn<string>(
                name: "MarketplaceUrl",
                table: "Items",
                type: "text",
                nullable: false,
                computedColumnSql: "'https://www.roblox.com/catalog/' || \"RobloxAssetId\"::text",
                stored: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldComputedColumnSql: "CASE WHEN \"ItemType\" = 'Bundle' THEN 'https://www.roblox.com/bundles/' ELSE 'https://www.roblox.com/catalog/' END || \"RobloxAssetId\"::text",
                oldStored: true);

            migrationBuilder.DropColumn(
                name: "BundleTypeId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "ItemType",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "ItemType",
                table: "CatalogImportFailures");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CatalogImportFailures",
                table: "CatalogImportFailures",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_RobloxAssetId",
                table: "Items",
                column: "RobloxAssetId",
                unique: true);
        }
    }
}



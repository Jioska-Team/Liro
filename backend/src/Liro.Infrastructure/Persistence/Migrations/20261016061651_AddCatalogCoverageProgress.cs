using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Liro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogCoverageProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverageStatus",
                table: "CatalogCheckpoints",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Unassessed");

            migrationBuilder.AddColumn<long>(
                name: "ItemFailures",
                table: "CatalogCheckpoints",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ItemsAdded",
                table: "CatalogCheckpoints",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ItemsSeen",
                table: "CatalogCheckpoints",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "CatalogCheckpoints",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PagesProcessed",
                table: "CatalogCheckpoints",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverageStatus",
                table: "CatalogCheckpoints");

            migrationBuilder.DropColumn(
                name: "ItemFailures",
                table: "CatalogCheckpoints");

            migrationBuilder.DropColumn(
                name: "ItemsAdded",
                table: "CatalogCheckpoints");

            migrationBuilder.DropColumn(
                name: "ItemsSeen",
                table: "CatalogCheckpoints");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "CatalogCheckpoints");

            migrationBuilder.DropColumn(
                name: "PagesProcessed",
                table: "CatalogCheckpoints");
        }
    }
}


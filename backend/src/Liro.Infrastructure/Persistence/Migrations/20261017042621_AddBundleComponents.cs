using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Liro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBundleComponents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long[]>(
                name: "BundleComponentAssetIds",
                table: "Items",
                type: "bigint[]",
                nullable: false,
                defaultValueSql: "'{}'::bigint[]");

            migrationBuilder.AddColumn<DateTime>(
                name: "BundleComponentsObservedAtUtc",
                table: "Items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BundleComponents",
                columns: table => new
                {
                    BundleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComponentItemId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BundleComponents", x => new { x.BundleId, x.ComponentItemId });
                    table.CheckConstraint("CK_BundleComponents_DifferentItems", "\"BundleId\" <> \"ComponentItemId\"");
                    table.ForeignKey(
                        name: "FK_BundleComponents_Items_BundleId",
                        column: x => x.BundleId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BundleComponents_Items_ComponentItemId",
                        column: x => x.ComponentItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BundleComponents_ComponentItemId",
                table: "BundleComponents",
                column: "ComponentItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BundleComponents");

            migrationBuilder.DropColumn(
                name: "BundleComponentAssetIds",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "BundleComponentsObservedAtUtc",
                table: "Items");
        }
    }
}

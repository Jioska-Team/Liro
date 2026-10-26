using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Liro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackLimitedConversions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BecameLimitedAtUtc",
                table: "Items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasBeenLimited",
                table: "Items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LimitedFirstObservedAtUtc",
                table: "Items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MarketStatusObservedAtUtc",
                table: "Items",
                type: "timestamp with time zone",
                nullable: true);

            // Preserve only known membership. Existing conversion/observation dates remain unknown.
            migrationBuilder.Sql("UPDATE \"Items\" SET \"HasBeenLimited\" = TRUE WHERE \"MarketStatus\" IN ('Limited', 'LimitedUnique')");
            migrationBuilder.CreateIndex(
                name: "IX_Items_BecameLimitedAtUtc_FirstSeenAt_Id",
                table: "Items",
                columns: new[] { "BecameLimitedAtUtc", "FirstSeenAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_FirstSeenAt_Id",
                table: "Items",
                columns: new[] { "FirstSeenAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Items_BecameLimitedAtUtc_FirstSeenAt_Id",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Items_FirstSeenAt_Id",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "BecameLimitedAtUtc",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "HasBeenLimited",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "LimitedFirstObservedAtUtc",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "MarketStatusObservedAtUtc",
                table: "Items");
        }
    }
}

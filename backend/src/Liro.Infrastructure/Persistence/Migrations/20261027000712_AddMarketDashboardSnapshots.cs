using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Liro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketDashboardSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketDashboardSnapshots",
                columns: table => new
                {
                    BucketUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LimitedsCount = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketDashboardSnapshots", x => x.BucketUtc);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketHistoryPoints_PeriodUtc_Source_Metric",
                table: "MarketHistoryPoints",
                columns: new[] { "PeriodUtc", "Source", "Metric" })
                .Annotation("Npgsql:IndexInclude", new[] { "ItemId", "Value", "ObservedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketDashboardSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_MarketHistoryPoints_PeriodUtc_Source_Metric",
                table: "MarketHistoryPoints");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Liro.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketTrackingAndGeneralCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogoGeneral",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Clave = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Valor = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    TipoDato = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Descripcion = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Activo = table.Column<bool>(type: "boolean", nullable: false),
                    FechaCreacion = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FechaModificacion = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreadoPor = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ModificadoPor = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogoGeneral", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketHistoryPoints",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Metric = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PeriodUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(28,6)", precision: 28, scale: 6, nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketHistoryPoints", x => new { x.ItemId, x.Source, x.Metric, x.PeriodUtc });
                    table.ForeignKey(
                        name: "FK_MarketHistoryPoints_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MarketRapSnapshots",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(28,6)", precision: 28, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketRapSnapshots", x => new { x.ItemId, x.ObservedAtUtc });
                    table.ForeignKey(
                        name: "FK_MarketRapSnapshots_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MarketWatches",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    NextCheckAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastErrorCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    RecentAveragePrice = table.Column<decimal>(type: "numeric(28,6)", precision: 28, scale: 6, nullable: true),
                    RapObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DailySales = table.Column<decimal>(type: "numeric(28,6)", precision: 28, scale: 6, nullable: true),
                    Activity = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketWatches", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_MarketWatches_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogoGeneral_Tipo_Clave",
                table: "CatalogoGeneral",
                columns: new[] { "Tipo", "Clave" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketWatches_NextCheckAtUtc_LeaseUntilUtc",
                table: "MarketWatches",
                columns: new[] { "NextCheckAtUtc", "LeaseUntilUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogoGeneral");

            migrationBuilder.DropTable(
                name: "MarketHistoryPoints");

            migrationBuilder.DropTable(
                name: "MarketRapSnapshots");

            migrationBuilder.DropTable(
                name: "MarketWatches");
        }
    }
}

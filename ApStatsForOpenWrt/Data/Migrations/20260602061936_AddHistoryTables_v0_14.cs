using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApStatsForOpenWrt.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoryTables_v0_14 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KpiHistorySnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OnlineCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StaleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    OfflineCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KpiHistorySnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RssiHistorySamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StationMac = table.Column<string>(type: "TEXT", maxLength: 17, nullable: false),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    NetworkId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rssi = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RssiHistorySamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RssiHistorySamples_WiFiNetworks_NetworkId",
                        column: x => x.NetworkId,
                        principalTable: "WiFiNetworks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RssiHistorySamples_WiFiNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "WiFiNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KpiHistorySnapshots_TimestampUtc",
                table: "KpiHistorySnapshots",
                column: "TimestampUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_RssiHistorySamples_NetworkId",
                table: "RssiHistorySamples",
                column: "NetworkId");

            migrationBuilder.CreateIndex(
                name: "IX_RssiHistorySamples_NodeId",
                table: "RssiHistorySamples",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_RssiHistorySamples_StationMac_TimestampUtc",
                table: "RssiHistorySamples",
                columns: new[] { "StationMac", "TimestampUtc" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_RssiHistorySamples_TimestampUtc",
                table: "RssiHistorySamples",
                column: "TimestampUtc",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KpiHistorySnapshots");

            migrationBuilder.DropTable(
                name: "RssiHistorySamples");
        }
    }
}

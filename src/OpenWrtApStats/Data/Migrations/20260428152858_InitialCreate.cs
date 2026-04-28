using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWrtApStats.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StringValue = table.Column<string>(type: "TEXT", nullable: true),
                    IntValue = table.Column<int>(type: "INTEGER", nullable: true),
                    BoolValue = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "WiFiNodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Password = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LastPollDate = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WiFiNodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WiFiNetworks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WiFiNodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    SSID = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    MacAddress = table.Column<string>(type: "TEXT", maxLength: 17, nullable: false),
                    TxPower = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WiFiNetworks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WiFiNetworks_WiFiNodes_WiFiNodeId",
                        column: x => x.WiFiNodeId,
                        principalTable: "WiFiNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WLANStations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WiFiNetworkId = table.Column<int>(type: "INTEGER", nullable: false),
                    MacAddress = table.Column<string>(type: "TEXT", maxLength: 17, nullable: false),
                    IPAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    TxBitrate = table.Column<double>(type: "REAL", nullable: false),
                    RxBitrate = table.Column<double>(type: "REAL", nullable: false),
                    RSS = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LastPollDate = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WLANStations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WLANStations_WiFiNetworks_WiFiNetworkId",
                        column: x => x.WiFiNetworkId,
                        principalTable: "WiFiNetworks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AppSettings",
                columns: new[] { "Key", "BoolValue", "IntValue", "StringValue" },
                values: new object[] { "PollingIntervalSeconds", null, 30, null });

            migrationBuilder.CreateIndex(
                name: "IX_WiFiNetworks_WiFiNodeId_MacAddress",
                table: "WiFiNetworks",
                columns: new[] { "WiFiNodeId", "MacAddress" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WLANStations_MacAddress",
                table: "WLANStations",
                column: "MacAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WLANStations_WiFiNetworkId",
                table: "WLANStations",
                column: "WiFiNetworkId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "WLANStations");

            migrationBuilder.DropTable(
                name: "WiFiNetworks");

            migrationBuilder.DropTable(
                name: "WiFiNodes");
        }
    }
}

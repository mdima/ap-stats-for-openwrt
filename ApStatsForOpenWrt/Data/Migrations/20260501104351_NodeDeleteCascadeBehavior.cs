using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApStatsForOpenWrt.Data.Migrations
{
    /// <inheritdoc />
    public partial class NodeDeleteCascadeBehavior : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WLANStations_WiFiNetworks_WiFiNetworkId",
                table: "WLANStations");

            migrationBuilder.AlterColumn<int>(
                name: "WiFiNetworkId",
                table: "WLANStations",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddForeignKey(
                name: "FK_WLANStations_WiFiNetworks_WiFiNetworkId",
                table: "WLANStations",
                column: "WiFiNetworkId",
                principalTable: "WiFiNetworks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WLANStations_WiFiNetworks_WiFiNetworkId",
                table: "WLANStations");

            migrationBuilder.AlterColumn<int>(
                name: "WiFiNetworkId",
                table: "WLANStations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WLANStations_WiFiNetworks_WiFiNetworkId",
                table: "WLANStations",
                column: "WiFiNetworkId",
                principalTable: "WiFiNetworks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

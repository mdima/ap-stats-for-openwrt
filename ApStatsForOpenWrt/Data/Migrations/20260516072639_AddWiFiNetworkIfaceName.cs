using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApStatsForOpenWrt.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWiFiNetworkIfaceName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IfaceName",
                table: "WiFiNetworks",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IfaceName",
                table: "WiFiNetworks");
        }
    }
}

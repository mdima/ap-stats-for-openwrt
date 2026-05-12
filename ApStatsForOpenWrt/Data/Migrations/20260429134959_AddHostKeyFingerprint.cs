using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApStatsForOpenWrt.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHostKeyFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HostKeyFingerprint",
                table: "WiFiNodes",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HostKeyFingerprint",
                table: "WiFiNodes");
        }
    }
}

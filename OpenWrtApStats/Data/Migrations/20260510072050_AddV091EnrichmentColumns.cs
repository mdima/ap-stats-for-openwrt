using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OpenWrtApStats.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddV091EnrichmentColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Phase 9 D-07 (migration step order); D-08 (raw-SQL backfill); D-09 (AppSetting InsertData).
            // Order: AddColumn x4 (SubnetScanRanges, SubnetScanEnabled, LastEnrichmentRefreshAt,
            // IsRandomMac) → raw-SQL IsRandomMac backfill → InsertData x2 AppSetting seeds.
            migrationBuilder.AddColumn<string>(
                name: "SubnetScanRanges",
                table: "WiFiNodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SubnetScanEnabled",
                table: "WiFiNodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastEnrichmentRefreshAt",
                table: "WiFiNodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRandomMac",
                table: "WLANStations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // D-08 verbatim: U/L bit on MAC byte 0 — IsRandomMac == ((value(byte0) & 2) != 0).
            // MacAddress is uppercase-at-boundary (Phase 2 D-07), so the alphabet is uppercase
            // hex. instr returns 1-indexed; subtract 1 to get the integer value of the second
            // hex char (the low nibble of byte 0); bit 1 (0x02) is the U/L bit.
            migrationBuilder.Sql("""
                UPDATE WLANStations
                SET IsRandomMac = ((instr('0123456789ABCDEF', substr(MacAddress, 2, 1)) - 1) & 2) <> 0
                WHERE MacAddress IS NOT NULL AND length(MacAddress) >= 2;
                """);

            // D-09: seed RefreshIntervalSeconds=3600 and LocalDomainSuffixes="[]".
            migrationBuilder.InsertData(
                table: "AppSettings",
                columns: new[] { "Key", "BoolValue", "IntValue", "StringValue" },
                values: new object[] { "RefreshIntervalSeconds", null, 3600, null });

            migrationBuilder.InsertData(
                table: "AppSettings",
                columns: new[] { "Key", "BoolValue", "IntValue", "StringValue" },
                values: new object[] { "LocalDomainSuffixes", null, null, "[]" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse order: DeleteData seeds first (LocalDomainSuffixes, then
            // RefreshIntervalSeconds), then DropColumn x4 in reverse of Up()
            // (IsRandomMac, LastEnrichmentRefreshAt, SubnetScanEnabled, SubnetScanRanges).
            // No backfill rollback needed — the IsRandomMac column drop erases the data.
            migrationBuilder.DeleteData(
                table: "AppSettings",
                keyColumn: "Key",
                keyValue: "LocalDomainSuffixes");

            migrationBuilder.DeleteData(
                table: "AppSettings",
                keyColumn: "Key",
                keyValue: "RefreshIntervalSeconds");

            migrationBuilder.DropColumn(
                name: "IsRandomMac",
                table: "WLANStations");

            migrationBuilder.DropColumn(
                name: "LastEnrichmentRefreshAt",
                table: "WiFiNodes");

            migrationBuilder.DropColumn(
                name: "SubnetScanEnabled",
                table: "WiFiNodes");

            migrationBuilder.DropColumn(
                name: "SubnetScanRanges",
                table: "WiFiNodes");
        }
    }
}

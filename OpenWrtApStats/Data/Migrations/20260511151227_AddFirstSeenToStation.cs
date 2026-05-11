using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWrtApStats.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFirstSeenToStation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // v0.9.2 — add FirstSeen (UTC, non-nullable, INSERT-only per ORCH-02) and backfill
            // pre-existing rows from LastPollDate (best lower bound for stations first observed
            // before this milestone, per seed file locked design). Down() drops the column —
            // no backfill rollback needed because the column drop erases the data, matching the
            // v0.9.1 AddV091EnrichmentColumns pattern.
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSeen",
                table: "WLANStations",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.Sql("""
                UPDATE "WLANStations"
                SET "FirstSeen" = "LastPollDate"
                WHERE "FirstSeen" = '0001-01-01 00:00:00';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstSeen",
                table: "WLANStations");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddFlightSyncSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAtUtc",
                table: "flights",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSyncedUtc",
                table: "flights",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextSyncDueUtc",
                table: "flights",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduleMissingSinceUtc",
                table: "flights",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncMode",
                table: "flights",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_flight_next_sync_due",
                table: "flights",
                column: "NextSyncDueUtc");

            // Every tracked flight is due at once, so the first loop plans it (spec §2.1). Others stay null.
            migrationBuilder.Sql("""
                UPDATE flights f
                SET "NextSyncDueUtc" = now()
                WHERE EXISTS (SELECT 1 FROM traveler_flights tf WHERE tf."FlightId" = f."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_flight_next_sync_due",
                table: "flights");

            migrationBuilder.DropColumn(
                name: "CancelledAtUtc",
                table: "flights");

            migrationBuilder.DropColumn(
                name: "LastSyncedUtc",
                table: "flights");

            migrationBuilder.DropColumn(
                name: "NextSyncDueUtc",
                table: "flights");

            migrationBuilder.DropColumn(
                name: "ScheduleMissingSinceUtc",
                table: "flights");

            migrationBuilder.DropColumn(
                name: "SyncMode",
                table: "flights");
        }
    }
}

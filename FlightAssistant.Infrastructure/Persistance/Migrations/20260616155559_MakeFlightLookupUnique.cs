using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class MakeFlightLookupUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_flight_lookup",
                table: "flights");

            migrationBuilder.CreateIndex(
                name: "ix_flight_lookup",
                table: "flights",
                columns: new[] { "AirlineId", "FlightNumber", "ServiceDateUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_flight_lookup",
                table: "flights");

            migrationBuilder.CreateIndex(
                name: "ix_flight_lookup",
                table: "flights",
                columns: new[] { "AirlineId", "FlightNumber", "ServiceDateUtc" });
        }
    }
}

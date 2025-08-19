using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class ConnectFlightAndAirlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_flights_airlines_AirlineId1",
                table: "flights");

            migrationBuilder.DropIndex(
                name: "IX_flights_AirlineId1",
                table: "flights");

            migrationBuilder.DropColumn(
                name: "AirlineId1",
                table: "flights");

            migrationBuilder.RenameIndex(
                name: "IX_airlines_AllianceCode",
                table: "airlines",
                newName: "ix_airlines_alliance");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                name: "ix_airlines_alliance",
                table: "airlines",
                newName: "IX_airlines_AllianceCode");

            migrationBuilder.AddColumn<int>(
                name: "AirlineId1",
                table: "flights",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_flights_AirlineId1",
                table: "flights",
                column: "AirlineId1");

            migrationBuilder.AddForeignKey(
                name: "FK_flights_airlines_AirlineId1",
                table: "flights",
                column: "AirlineId1",
                principalTable: "airlines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

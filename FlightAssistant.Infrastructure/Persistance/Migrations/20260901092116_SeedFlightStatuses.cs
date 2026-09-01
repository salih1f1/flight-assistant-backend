using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class SeedFlightStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "flight_statuses",
                columns: new[] { "Id", "Code", "Description", "Name" },
                values: new object[,]
                {
                    { 1, "SCH", "Flight is scheduled and has not yet departed.", "Scheduled" },
                    { 2, "DEP", "Flight has departed and is en route to its destination.", "Departed" },
                    { 3, "ARR", "Flight has arrived at the destination gate.", "Arrived" },
                    { 4, "DLY", "Flight is delayed from its scheduled departure or arrival time.", "Delayed" },
                    { 5, "CAN", "Flight has been cancelled.", "Cancelled" },
                    { 6, "DIV", "Flight has been diverted to an airport other than its scheduled destination.", "Diverted" },
                    { 7, "UNK", "Status could not be determined, or the provider reported a status this system does not recognise.", "Unknown" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "flight_statuses",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "flight_statuses",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "flight_statuses",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "flight_statuses",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.DeleteData(
                table: "flight_statuses",
                keyColumn: "Id",
                keyValue: 5);

            migrationBuilder.DeleteData(
                table: "flight_statuses",
                keyColumn: "Id",
                keyValue: 6);

            migrationBuilder.DeleteData(
                table: "flight_statuses",
                keyColumn: "Id",
                keyValue: 7);
        }
    }
}

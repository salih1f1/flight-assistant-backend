using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class InitFlightsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "flight_statuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flight_statuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "flights",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AirlineId = table.Column<int>(type: "integer", nullable: false),
                    FlightNumber = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    DepartureAirportId = table.Column<int>(type: "integer", nullable: false),
                    ArrivalAirportId = table.Column<int>(type: "integer", nullable: false),
                    ServiceDateUtc = table.Column<DateOnly>(type: "date", nullable: false),
                    ScheduledDepartureUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScheduledArrivalUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstimatedDepartureUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EstimatedArrivalUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActualDepartureUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActualArrivalUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EquipmentAircraftIcao = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    ExternalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AirlineId1 = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_flights_airlines_AirlineId",
                        column: x => x.AirlineId,
                        principalTable: "airlines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_flights_airlines_AirlineId1",
                        column: x => x.AirlineId1,
                        principalTable: "airlines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_flights_airports_ArrivalAirportId",
                        column: x => x.ArrivalAirportId,
                        principalTable: "airports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_flights_airports_DepartureAirportId",
                        column: x => x.DepartureAirportId,
                        principalTable: "airports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "flight_status_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FlightId = table.Column<int>(type: "integer", nullable: false),
                    StatusId = table.Column<int>(type: "integer", nullable: false),
                    EffectiveAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    Source = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false, defaultValue: "manual"),
                    ExternalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Gate = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Terminal = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    BaggageClaim = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Remarks = table.Column<string>(type: "text", nullable: true),
                    RawPayloadJson = table.Column<string>(type: "jsonb", nullable: true),
                    DedupHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flight_status_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_flight_status_events_flight_statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "flight_statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_flight_status_events_flights_FlightId",
                        column: x => x.FlightId,
                        principalTable: "flights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "traveler_flights",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TravelerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlightId = table.Column<int>(type: "integer", nullable: false),
                    Notify = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_traveler_flights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_traveler_flights_flights_FlightId",
                        column: x => x.FlightId,
                        principalTable: "flights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_traveler_flights_travelers_TravelerId",
                        column: x => x.TravelerId,
                        principalTable: "travelers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_flight_status_events_FlightId_Source_DedupHash",
                table: "flight_status_events",
                columns: new[] { "FlightId", "Source", "DedupHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flight_status_events_StatusId",
                table: "flight_status_events",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "ix_fse_latest",
                table: "flight_status_events",
                columns: new[] { "FlightId", "EffectiveAtUtc", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_fse_status_time",
                table: "flight_status_events",
                columns: new[] { "FlightId", "StatusId", "EffectiveAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_flight_statuses_Code",
                table: "flight_statuses",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_flight_arr_day",
                table: "flights",
                columns: new[] { "ArrivalAirportId", "ServiceDateUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_flight_dep_day",
                table: "flights",
                columns: new[] { "DepartureAirportId", "ServiceDateUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_flight_lookup",
                table: "flights",
                columns: new[] { "AirlineId", "FlightNumber", "ServiceDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_flights_AirlineId1",
                table: "flights",
                column: "AirlineId1");

            migrationBuilder.CreateIndex(
                name: "IX_traveler_flights_FlightId",
                table: "traveler_flights",
                column: "FlightId");

            migrationBuilder.CreateIndex(
                name: "IX_traveler_flights_TravelerId_FlightId",
                table: "traveler_flights",
                columns: new[] { "TravelerId", "FlightId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flight_status_events");

            migrationBuilder.DropTable(
                name: "traveler_flights");

            migrationBuilder.DropTable(
                name: "flight_statuses");

            migrationBuilder.DropTable(
                name: "flights");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddAirportsAirlinesAndAlliances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "airports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Iata = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Icao = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    City = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CountryCode = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    ElevationFt = table.Column<int>(type: "integer", nullable: true),
                    Timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_airports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_airports_countries_CountryCode",
                        column: x => x.CountryCode,
                        principalTable: "countries",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alliances",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alliances", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "airlines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Iata = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Icao = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CountryCode = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    AllianceCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_airlines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_airlines_alliances_AllianceCode",
                        column: x => x.AllianceCode,
                        principalTable: "alliances",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_airlines_countries_CountryCode",
                        column: x => x.CountryCode,
                        principalTable: "countries",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_airlines_AllianceCode",
                table: "airlines",
                column: "AllianceCode");

            migrationBuilder.CreateIndex(
                name: "IX_airlines_CountryCode",
                table: "airlines",
                column: "CountryCode");

            migrationBuilder.CreateIndex(
                name: "IX_airlines_Iata",
                table: "airlines",
                column: "Iata",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_airlines_Icao",
                table: "airlines",
                column: "Icao",
                unique: true,
                filter: "\"Icao\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_airports_CountryCode_City",
                table: "airports",
                columns: new[] { "CountryCode", "City" });

            migrationBuilder.CreateIndex(
                name: "IX_airports_Iata",
                table: "airports",
                column: "Iata",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_airports_Icao",
                table: "airports",
                column: "Icao",
                unique: true,
                filter: "\"Icao\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_alliances_Code",
                table: "alliances",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "airlines");

            migrationBuilder.DropTable(
                name: "airports");

            migrationBuilder.DropTable(
                name: "alliances");
        }
    }
}

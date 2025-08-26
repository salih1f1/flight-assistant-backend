using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class InitLounges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lounge_providers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lounge_providers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "lounges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AirportId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Terminal = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LocationHint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Hours = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsAirside = table.Column<bool>(type: "boolean", nullable: false),
                    IsDomesticOnly = table.Column<bool>(type: "boolean", nullable: false),
                    IsInternationalOnly = table.Column<bool>(type: "boolean", nullable: false),
                    Amenities = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lounges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lounges_airports_AirportId",
                        column: x => x.AirportId,
                        principalTable: "airports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lounge_provider_links",
                columns: table => new
                {
                    LoungeId = table.Column<int>(type: "integer", nullable: false),
                    ProviderId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lounge_provider_links", x => new { x.LoungeId, x.ProviderId, x.Role });
                    table.ForeignKey(
                        name: "FK_lounge_provider_links_lounge_providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "lounge_providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_lounge_provider_links_lounges_LoungeId",
                        column: x => x.LoungeId,
                        principalTable: "lounges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lounge_provider_links_ProviderId_Role",
                table: "lounge_provider_links",
                columns: new[] { "ProviderId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_lounge_providers_Code",
                table: "lounge_providers",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lounges_AirportId_Terminal_Name",
                table: "lounges",
                columns: new[] { "AirportId", "Terminal", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lounge_provider_links");

            migrationBuilder.DropTable(
                name: "lounge_providers");

            migrationBuilder.DropTable(
                name: "lounges");
        }
    }
}

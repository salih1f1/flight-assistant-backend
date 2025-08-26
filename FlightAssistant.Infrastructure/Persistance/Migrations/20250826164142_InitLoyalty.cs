using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class InitLoyalty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "loyalty_program_kinds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_program_kinds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_programs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    KindId = table.Column<int>(type: "integer", nullable: false),
                    AirlineId = table.Column<int>(type: "integer", nullable: true),
                    AllianceCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_programs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_loyalty_programs_airlines_AirlineId",
                        column: x => x.AirlineId,
                        principalTable: "airlines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_loyalty_programs_loyalty_program_kinds_KindId",
                        column: x => x.KindId,
                        principalTable: "loyalty_program_kinds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_tiers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProgramId = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    AllianceCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    AllianceTierCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_tiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_loyalty_tiers_loyalty_programs_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "loyalty_programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "traveler_loyalty_memberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TravelerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<int>(type: "integer", nullable: false),
                    TierId = table.Column<int>(type: "integer", nullable: true),
                    AccountNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TierExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PointsExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_traveler_loyalty_memberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_traveler_loyalty_memberships_loyalty_programs_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "loyalty_programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_traveler_loyalty_memberships_loyalty_tiers_TierId",
                        column: x => x.TierId,
                        principalTable: "loyalty_tiers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_traveler_loyalty_memberships_travelers_TravelerId",
                        column: x => x.TravelerId,
                        principalTable: "travelers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_program_kinds_Code",
                table: "loyalty_program_kinds",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_programs_AirlineId",
                table: "loyalty_programs",
                column: "AirlineId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_programs_Code",
                table: "loyalty_programs",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_programs_KindId",
                table: "loyalty_programs",
                column: "KindId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_tiers_ProgramId_Code",
                table: "loyalty_tiers",
                columns: new[] { "ProgramId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_tiers_ProgramId_Rank",
                table: "loyalty_tiers",
                columns: new[] { "ProgramId", "Rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_traveler_loyalty_memberships_ProgramId",
                table: "traveler_loyalty_memberships",
                column: "ProgramId");

            migrationBuilder.CreateIndex(
                name: "IX_traveler_loyalty_memberships_TierId",
                table: "traveler_loyalty_memberships",
                column: "TierId");

            migrationBuilder.CreateIndex(
                name: "IX_traveler_loyalty_memberships_TravelerId_ProgramId",
                table: "traveler_loyalty_memberships",
                columns: new[] { "TravelerId", "ProgramId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "traveler_loyalty_memberships");

            migrationBuilder.DropTable(
                name: "loyalty_tiers");

            migrationBuilder.DropTable(
                name: "loyalty_programs");

            migrationBuilder.DropTable(
                name: "loyalty_program_kinds");
        }
    }
}

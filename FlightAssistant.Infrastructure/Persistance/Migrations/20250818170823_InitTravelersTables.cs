using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class InitTravelersTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "travelers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CountryCode = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: true),
                    NationalityCountryCode = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: true),
                    ResidenceCountryCode = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_travelers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_travelers_countries_NationalityCountryCode",
                        column: x => x.NationalityCountryCode,
                        principalTable: "countries",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_travelers_countries_ResidenceCountryCode",
                        column: x => x.ResidenceCountryCode,
                        principalTable: "countries",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TravelerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Token = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_device_tokens_travelers_TravelerId",
                        column: x => x.TravelerId,
                        principalTable: "travelers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_tokens_TravelerId_Token",
                table: "device_tokens",
                columns: new[] { "TravelerId", "Token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_travelers_Email",
                table: "travelers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_travelers_NationalityCountryCode",
                table: "travelers",
                column: "NationalityCountryCode");

            migrationBuilder.CreateIndex(
                name: "IX_travelers_ResidenceCountryCode",
                table: "travelers",
                column: "ResidenceCountryCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_tokens");

            migrationBuilder.DropTable(
                name: "travelers");
        }
    }
}

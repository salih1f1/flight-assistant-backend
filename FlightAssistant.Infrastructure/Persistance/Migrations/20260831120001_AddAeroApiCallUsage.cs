using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddAeroApiCallUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "aeroapi_calls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointTemplate = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Caller = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CalledUtc = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aeroapi_calls", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_aeroapi_calls_CalledUtc",
                table: "aeroapi_calls",
                column: "CalledUtc");

            migrationBuilder.CreateIndex(
                name: "IX_aeroapi_calls_CalledUtc_EndpointTemplate",
                table: "aeroapi_calls",
                columns: new[] { "CalledUtc", "EndpointTemplate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "aeroapi_calls");
        }
    }
}

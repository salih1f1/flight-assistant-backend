using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_routes",
                columns: table => new
                {
                    job = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    providers = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    changed_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    changed_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_routes", x => x.job);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_routes");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "verification_requests",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    entity_key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    sources = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_utc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    differences = table.Column<string>(type: "jsonb", nullable: true),
                    source_values = table.Column<string>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    estimated_cost_usd = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    requested_utc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    started_utc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    completed_utc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_verification_requests_claim",
                table: "verification_requests",
                columns: new[] { "status", "next_attempt_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_verification_requests_open_entity",
                table: "verification_requests",
                columns: new[] { "entity_type", "entity_key" },
                unique: true,
                filter: "\"status\" IN ('Pending', 'Running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "verification_requests");
        }
    }
}

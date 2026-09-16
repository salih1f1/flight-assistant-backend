using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationRequestFailedSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "failed_source",
                table: "verification_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_verification_requests_error_attribution",
                table: "verification_requests",
                column: "completed_utc",
                filter: "\"status\" = 'Error'")
                .Annotation("Npgsql:IndexInclude", new[] { "failed_source", "entity_type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_verification_requests_error_attribution",
                table: "verification_requests");

            migrationBuilder.DropColumn(
                name: "failed_source",
                table: "verification_requests");
        }
    }
}

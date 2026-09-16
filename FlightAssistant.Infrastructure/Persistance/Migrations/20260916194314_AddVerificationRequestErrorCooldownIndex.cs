using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationRequestErrorCooldownIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_verification_requests_error_cooldown",
                table: "verification_requests",
                columns: new[] { "entity_type", "entity_key", "completed_utc" },
                filter: "\"status\" = 'Error'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_verification_requests_error_cooldown",
                table: "verification_requests");
        }
    }
}

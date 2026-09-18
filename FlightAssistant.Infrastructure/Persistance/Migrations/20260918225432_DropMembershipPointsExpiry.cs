using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <summary>Points expiry was never read by anything — no notification, no eligibility rule,
    /// no export — so it leaves the database with #484. Dropping the column discards whatever
    /// dates it held; <c>Down</c> brings the column back empty, not the values.</summary>
    public partial class DropMembershipPointsExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PointsExpiryDate",
                table: "traveler_loyalty_memberships");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "PointsExpiryDate",
                table: "traveler_loyalty_memberships",
                type: "date",
                nullable: true);
        }
    }
}

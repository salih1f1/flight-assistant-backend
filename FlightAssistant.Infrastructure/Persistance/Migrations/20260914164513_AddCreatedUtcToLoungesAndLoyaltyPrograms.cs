using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatedUtcToLoungesAndLoyaltyPrograms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "created_utc",
                table: "loyalty_programs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "created_utc",
                table: "lounges",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "created_utc",
                table: "loyalty_programs");

            migrationBuilder.DropColumn(
                name: "created_utc",
                table: "lounges");
        }
    }
}

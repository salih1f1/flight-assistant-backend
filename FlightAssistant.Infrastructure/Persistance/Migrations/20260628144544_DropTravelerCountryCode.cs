using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class DropTravelerCountryCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CountryCode",
                table: "travelers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                table: "travelers",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: true);
        }
    }
}

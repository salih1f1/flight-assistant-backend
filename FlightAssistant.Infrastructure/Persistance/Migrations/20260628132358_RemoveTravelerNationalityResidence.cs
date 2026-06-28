using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTravelerNationalityResidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_travelers_countries_NationalityCountryCode",
                table: "travelers");

            migrationBuilder.DropForeignKey(
                name: "FK_travelers_countries_ResidenceCountryCode",
                table: "travelers");

            migrationBuilder.DropIndex(
                name: "IX_travelers_NationalityCountryCode",
                table: "travelers");

            migrationBuilder.DropIndex(
                name: "IX_travelers_ResidenceCountryCode",
                table: "travelers");

            migrationBuilder.DropColumn(
                name: "NationalityCountryCode",
                table: "travelers");

            migrationBuilder.DropColumn(
                name: "ResidenceCountryCode",
                table: "travelers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NationalityCountryCode",
                table: "travelers",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidenceCountryCode",
                table: "travelers",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_travelers_NationalityCountryCode",
                table: "travelers",
                column: "NationalityCountryCode");

            migrationBuilder.CreateIndex(
                name: "IX_travelers_ResidenceCountryCode",
                table: "travelers",
                column: "ResidenceCountryCode");

            migrationBuilder.AddForeignKey(
                name: "FK_travelers_countries_NationalityCountryCode",
                table: "travelers",
                column: "NationalityCountryCode",
                principalTable: "countries",
                principalColumn: "Code",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_travelers_countries_ResidenceCountryCode",
                table: "travelers",
                column: "ResidenceCountryCode",
                principalTable: "countries",
                principalColumn: "Code",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

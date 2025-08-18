using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class InitCountries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "countries",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Alpha3 = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    NumericCode = table.Column<int>(type: "integer", nullable: true),
                    Region = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Subregion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsEU = table.Column<bool>(type: "boolean", nullable: true),
                    IsSchengen = table.Column<bool>(type: "boolean", nullable: true),
                    IsEEA = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_countries", x => x.Code);
                });

            migrationBuilder.CreateIndex(
                name: "IX_countries_Alpha3",
                table: "countries",
                column: "Alpha3",
                unique: true,
                filter: "\"Alpha3\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_countries_NumericCode",
                table: "countries",
                column: "NumericCode",
                unique: true,
                filter: "\"NumericCode\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "countries");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddAllianceSurrogateId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_alliances",
                table: "alliances");

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "alliances",
                type: "integer",
                nullable: false,
                defaultValue: 0)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_alliances_Code",
                table: "alliances",
                column: "Code");

            migrationBuilder.AddPrimaryKey(
                name: "PK_alliances",
                table: "alliances",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "AK_alliances_Code",
                table: "alliances");

            migrationBuilder.DropPrimaryKey(
                name: "PK_alliances",
                table: "alliances");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "alliances");

            migrationBuilder.AddPrimaryKey(
                name: "PK_alliances",
                table: "alliances",
                column: "Code");
        }
    }
}

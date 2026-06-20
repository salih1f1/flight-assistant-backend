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
            // The airlines FK references alliances(Code), which is currently the
            // primary key. Drop it before swapping the PK to the new surrogate Id,
            // then recreate it against the AK_alliances_Code alternate key.
            migrationBuilder.DropForeignKey(
                name: "FK_airlines_alliances_AllianceCode",
                table: "airlines");

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

            migrationBuilder.AddForeignKey(
                name: "FK_airlines_alliances_AllianceCode",
                table: "airlines",
                column: "AllianceCode",
                principalTable: "alliances",
                principalColumn: "Code",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_airlines_alliances_AllianceCode",
                table: "airlines");

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

            migrationBuilder.AddForeignKey(
                name: "FK_airlines_alliances_AllianceCode",
                table: "airlines",
                column: "AllianceCode",
                principalTable: "alliances",
                principalColumn: "Code",
                onDelete: ReferentialAction.SetNull);
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <summary>
    /// An alliance is an airline alliance, so it belongs on the airline (#504). The alliance facts
    /// currently live only on the programs, so this migration moves them down before dropping the
    /// column: each program's alliance is written onto its airline where the airline has none.
    ///
    /// <para>A program's code is normalised — trimmed and upper-cased — before it is matched and
    /// copied. Unlike <c>airlines.AllianceCode</c>, the program column never had a value converter:
    /// it was free text typed by admins, so a real alliance held as <c>"ow"</c>, <c>" OW"</c> or
    /// any other casing/whitespace variant must be normalised here or it is indistinguishable from
    /// a junk code and is dropped with the column.</para>
    ///
    /// <para>What it deliberately does not do. It never overwrites an alliance an admin set on the
    /// airline, and it cannot copy a code that, once normalised, names no row in <c>alliances</c> —
    /// that is the foreign key on <c>airlines.AllianceCode</c>, and such a value is discarded with
    /// the column. Where two programs of one airline disagree, the lowest id among the programs
    /// whose normalised code names a real alliance wins, so an unusable code (free text such as the
    /// never-valid "STAR") cannot shadow a usable one, and the result does not depend on row
    /// order.</para>
    ///
    /// <para><c>Down</c> brings the column back empty, not the values: by then the airline holds
    /// them.</para>
    /// </summary>
    public partial class RemoveLoyaltyProgramAllianceCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE airlines a
                SET "AllianceCode" = UPPER(BTRIM(p."AllianceCode"))
                FROM loyalty_programs p
                WHERE p."AirlineId" = a."Id"
                  AND a."AllianceCode" IS NULL
                  AND p."AllianceCode" IS NOT NULL
                  AND EXISTS (SELECT 1 FROM alliances al WHERE al."Code" = UPPER(BTRIM(p."AllianceCode")))
                  AND p."Id" = (
                      SELECT MIN(q."Id") FROM loyalty_programs q
                      WHERE q."AirlineId" = a."Id"
                        AND EXISTS (SELECT 1 FROM alliances al WHERE al."Code" = UPPER(BTRIM(q."AllianceCode")))
                  );
                """);

            migrationBuilder.DropColumn(
                name: "AllianceCode",
                table: "loyalty_programs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllianceCode",
                table: "loyalty_programs",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }
    }
}

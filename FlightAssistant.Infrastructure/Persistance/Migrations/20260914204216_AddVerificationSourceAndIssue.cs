using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationSourceAndIssue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "verification_issue",
                table: "loyalty_programs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_source",
                table: "loyalty_programs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_issue",
                table: "loyalty_program_kinds",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_source",
                table: "loyalty_program_kinds",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_issue",
                table: "lounges",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_source",
                table: "lounges",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_issue",
                table: "lounge_providers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_source",
                table: "lounge_providers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_issue",
                table: "countries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_source",
                table: "countries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_issue",
                table: "alliances",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_source",
                table: "alliances",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_issue",
                table: "airports",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_source",
                table: "airports",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_issue",
                table: "airlines",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_source",
                table: "airlines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // Backfill (#355 user decision): only the manual verify route has ever set
            // IsVerified, so every already-verified row was verified manually.
            migrationBuilder.Sql("UPDATE loyalty_programs SET verification_source = 'manual' WHERE is_verified = true;");
            migrationBuilder.Sql("UPDATE loyalty_program_kinds SET verification_source = 'manual' WHERE is_verified = true;");
            migrationBuilder.Sql("UPDATE lounges SET verification_source = 'manual' WHERE is_verified = true;");
            migrationBuilder.Sql("UPDATE lounge_providers SET verification_source = 'manual' WHERE is_verified = true;");
            migrationBuilder.Sql("UPDATE countries SET verification_source = 'manual' WHERE is_verified = true;");
            migrationBuilder.Sql("UPDATE alliances SET verification_source = 'manual' WHERE is_verified = true;");
            migrationBuilder.Sql("UPDATE airports SET verification_source = 'manual' WHERE is_verified = true;");
            migrationBuilder.Sql("UPDATE airlines SET verification_source = 'manual' WHERE is_verified = true;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "verification_issue",
                table: "loyalty_programs");

            migrationBuilder.DropColumn(
                name: "verification_source",
                table: "loyalty_programs");

            migrationBuilder.DropColumn(
                name: "verification_issue",
                table: "loyalty_program_kinds");

            migrationBuilder.DropColumn(
                name: "verification_source",
                table: "loyalty_program_kinds");

            migrationBuilder.DropColumn(
                name: "verification_issue",
                table: "lounges");

            migrationBuilder.DropColumn(
                name: "verification_source",
                table: "lounges");

            migrationBuilder.DropColumn(
                name: "verification_issue",
                table: "lounge_providers");

            migrationBuilder.DropColumn(
                name: "verification_source",
                table: "lounge_providers");

            migrationBuilder.DropColumn(
                name: "verification_issue",
                table: "countries");

            migrationBuilder.DropColumn(
                name: "verification_source",
                table: "countries");

            migrationBuilder.DropColumn(
                name: "verification_issue",
                table: "alliances");

            migrationBuilder.DropColumn(
                name: "verification_source",
                table: "alliances");

            migrationBuilder.DropColumn(
                name: "verification_issue",
                table: "airports");

            migrationBuilder.DropColumn(
                name: "verification_source",
                table: "airports");

            migrationBuilder.DropColumn(
                name: "verification_issue",
                table: "airlines");

            migrationBuilder.DropColumn(
                name: "verification_source",
                table: "airlines");
        }
    }
}

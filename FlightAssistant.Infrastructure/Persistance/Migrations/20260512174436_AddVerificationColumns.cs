using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_verified",
                table: "loyalty_programs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "verified_at",
                table: "loyalty_programs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verified_by",
                table: "loyalty_programs",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_verified",
                table: "loyalty_program_kinds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "verified_at",
                table: "loyalty_program_kinds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verified_by",
                table: "loyalty_program_kinds",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_verified",
                table: "lounges",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "verified_at",
                table: "lounges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verified_by",
                table: "lounges",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_verified",
                table: "lounge_providers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "verified_at",
                table: "lounge_providers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verified_by",
                table: "lounge_providers",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_verified",
                table: "countries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "verified_at",
                table: "countries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verified_by",
                table: "countries",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_verified",
                table: "alliances",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "verified_at",
                table: "alliances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verified_by",
                table: "alliances",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_verified",
                table: "airports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "verified_at",
                table: "airports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verified_by",
                table: "airports",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_verified",
                table: "airlines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "verified_at",
                table: "airlines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verified_by",
                table: "airlines",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_verified",
                table: "loyalty_programs");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "loyalty_programs");

            migrationBuilder.DropColumn(
                name: "verified_by",
                table: "loyalty_programs");

            migrationBuilder.DropColumn(
                name: "is_verified",
                table: "loyalty_program_kinds");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "loyalty_program_kinds");

            migrationBuilder.DropColumn(
                name: "verified_by",
                table: "loyalty_program_kinds");

            migrationBuilder.DropColumn(
                name: "is_verified",
                table: "lounges");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "lounges");

            migrationBuilder.DropColumn(
                name: "verified_by",
                table: "lounges");

            migrationBuilder.DropColumn(
                name: "is_verified",
                table: "lounge_providers");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "lounge_providers");

            migrationBuilder.DropColumn(
                name: "verified_by",
                table: "lounge_providers");

            migrationBuilder.DropColumn(
                name: "is_verified",
                table: "countries");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "countries");

            migrationBuilder.DropColumn(
                name: "verified_by",
                table: "countries");

            migrationBuilder.DropColumn(
                name: "is_verified",
                table: "alliances");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "alliances");

            migrationBuilder.DropColumn(
                name: "verified_by",
                table: "alliances");

            migrationBuilder.DropColumn(
                name: "is_verified",
                table: "airports");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "airports");

            migrationBuilder.DropColumn(
                name: "verified_by",
                table: "airports");

            migrationBuilder.DropColumn(
                name: "is_verified",
                table: "airlines");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "airlines");

            migrationBuilder.DropColumn(
                name: "verified_by",
                table: "airlines");
        }
    }
}

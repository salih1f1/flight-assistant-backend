using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class DropUnwrittenEntityFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "travelers");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "travelers");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "flights");

            migrationBuilder.DropColumn(
                name: "ExternalIds",
                table: "countries");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "countries");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "countries");

            migrationBuilder.DropColumn(
                name: "SourceSystem",
                table: "countries");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "countries");

            migrationBuilder.DropColumn(
                name: "ExternalIds",
                table: "alliances");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "alliances");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "alliances");

            migrationBuilder.DropColumn(
                name: "SourceSystem",
                table: "alliances");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "alliances");

            migrationBuilder.DropColumn(
                name: "ExternalIds",
                table: "airports");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "airports");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "airports");

            migrationBuilder.DropColumn(
                name: "SourceSystem",
                table: "airports");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "airports");

            migrationBuilder.DropColumn(
                name: "ExternalIds",
                table: "airlines");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "airlines");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "airlines");

            migrationBuilder.DropColumn(
                name: "SourceSystem",
                table: "airlines");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "airlines");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                table: "travelers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedBy",
                table: "travelers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "flights",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalIds",
                table: "countries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                table: "countries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "countries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSystem",
                table: "countries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "countries",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExternalIds",
                table: "alliances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                table: "alliances",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "alliances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSystem",
                table: "alliances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "alliances",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExternalIds",
                table: "airports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                table: "airports",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "airports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSystem",
                table: "airports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "airports",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExternalIds",
                table: "airlines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                table: "airlines",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "airlines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSystem",
                table: "airlines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "airlines",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}

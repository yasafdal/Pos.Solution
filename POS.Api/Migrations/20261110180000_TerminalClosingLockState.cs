using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Api.Migrations
{
    /// <inheritdoc />
    public partial class TerminalClosingLockState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClosedBusinessDate",
                table: "Terminals",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosingLockedAtUtc",
                table: "Terminals",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosingLockedByCashier",
                table: "Terminals",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosingUnlockAtUtc",
                table: "Terminals",
                type: "timestamp without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedBusinessDate",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "ClosingLockedAtUtc",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "ClosingLockedByCashier",
                table: "Terminals");

            migrationBuilder.DropColumn(
                name: "ClosingUnlockAtUtc",
                table: "Terminals");
        }
    }
}

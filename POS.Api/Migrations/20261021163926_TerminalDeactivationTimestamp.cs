using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Api.Migrations
{
    /// <inheritdoc />
    public partial class TerminalDeactivationTimestamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Terminals' AND column_name = 'DeactivatedAt') THEN
        ALTER TABLE ""Terminals"" ADD COLUMN ""DeactivatedAt"" timestamp without time zone NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Outlets' AND column_name = 'DeactivatedAt') THEN
        ALTER TABLE ""Outlets"" ADD COLUMN ""DeactivatedAt"" timestamp without time zone NULL;
    END IF;
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Deactivation history must not be silently discarded. Restore a reviewed backup while writes are stopped, or forward-fix.");
        }
    }
}

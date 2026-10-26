using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Api.Migrations
{
    /// <inheritdoc />
    public partial class ConfirmedOpeningFloat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "OpeningConfirmed" boolean NOT NULL DEFAULT false;
                ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "ClientShiftId" character varying(100) NULL;
                DO $opening_readiness$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "CashDrawerShifts" WHERE "Status" = 'OPEN' GROUP BY "TerminalId" HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Opening float migration blocked: reconcile duplicate OPEN shifts per terminal first';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "CashDrawerShifts" WHERE "ClientShiftId" IS NOT NULL GROUP BY "ClientShiftId" HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Opening float migration blocked: reconcile duplicate ClientShiftId values first';
                    END IF;
                END $opening_readiness$;
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_CashDrawerShifts_ClientShiftId" ON "CashDrawerShifts" ("ClientShiftId") WHERE "ClientShiftId" IS NOT NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_CashDrawerShifts_OpenTerminal" ON "CashDrawerShifts" ("TerminalId") WHERE "Status" = 'OPEN';
                -- Do not infer historical confirmation. Retain legacy indexes: strictly additive.
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Opening identities and confirmations are audit data. Roll back application binaries only; retain the additive schema or restore a verified backup under DBA control.");
        }
    }
}

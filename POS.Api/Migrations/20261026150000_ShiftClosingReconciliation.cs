using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Api.Migrations
{
    /// <inheritdoc />
    public partial class ShiftClosingReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "ClientCloseId" character varying(100) NULL;
                ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "ClosingDenominationsJson" text NULL;
                ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "ClosingOriginalDeclarationJson" text NULL;
                ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "ClosingPaymentReconciliationsJson" text NULL;
                ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "ClosingRecountHistoryJson" text NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_CashDrawerShifts_ClientCloseId" ON "CashDrawerShifts" ("ClientCloseId") WHERE "ClientCloseId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Shift closing denomination and reconciliation records are audit data. Retain the additive schema or restore a verified backup under DBA control.");
        }
    }
}

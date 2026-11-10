using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Api.Migrations
{
    public partial class InitialCashFundManagement : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Outlets" ADD COLUMN IF NOT EXISTS "DefaultOpeningFund" numeric(18,2) NULL;
                ALTER TABLE "Outlets" ADD COLUMN IF NOT EXISTS "CashFundUpdatedBy" character varying(450) NULL;
                ALTER TABLE "Outlets" ADD COLUMN IF NOT EXISTS "CashFundUpdatedAt" timestamp without time zone NULL;
                ALTER TABLE "Terminals" ADD COLUMN IF NOT EXISTS "OpeningFundOverride" numeric(18,2) NULL;
                ALTER TABLE "Terminals" ADD COLUMN IF NOT EXISTS "CashFundUpdatedBy" character varying(450) NULL;
                ALTER TABLE "Terminals" ADD COLUMN IF NOT EXISTS "CashFundUpdatedAt" timestamp without time zone NULL;
                ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "ExpectedOpeningFund" numeric(18,2) NULL;
                ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "OpeningDiscrepancy" numeric(18,2) NULL;
                ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "OpeningDenominationsJson" text NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Additive cash fund management columns must be retained when rolling back application binaries.");
        }
    }
}

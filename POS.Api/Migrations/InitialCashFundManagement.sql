ALTER TABLE "Outlets" ADD COLUMN IF NOT EXISTS "DefaultOpeningFund" numeric(18,2) NULL;
ALTER TABLE "Outlets" ADD COLUMN IF NOT EXISTS "CashFundUpdatedBy" character varying(450) NULL;
ALTER TABLE "Outlets" ADD COLUMN IF NOT EXISTS "CashFundUpdatedAt" timestamp without time zone NULL;
ALTER TABLE "Terminals" ADD COLUMN IF NOT EXISTS "OpeningFundOverride" numeric(18,2) NULL;
ALTER TABLE "Terminals" ADD COLUMN IF NOT EXISTS "CashFundUpdatedBy" character varying(450) NULL;
ALTER TABLE "Terminals" ADD COLUMN IF NOT EXISTS "CashFundUpdatedAt" timestamp without time zone NULL;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "ExpectedOpeningFund" numeric(18,2) NULL;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "OpeningDiscrepancy" numeric(18,2) NULL;
ALTER TABLE "CashDrawerShifts" ADD COLUMN IF NOT EXISTS "OpeningDenominationsJson" text NULL;

-- =============================================================================
-- Migration: Customer Details on Receipt Toggle (Receipt Branding)
-- Target DB: PostgreSQL (POS.Api / POS.Web)
-- Note: Safe and idempotent. Defaults existing branches to TRUE (ON).
-- =============================================================================

BEGIN;

ALTER TABLE "Branches"
    ADD COLUMN IF NOT EXISTS "ShowCustomerDetailsOnReceipt" BOOLEAN NOT NULL DEFAULT TRUE;

UPDATE "Branches"
SET "ShowCustomerDetailsOnReceipt" = TRUE
WHERE "ShowCustomerDetailsOnReceipt" IS NULL;

COMMIT;

-- Verify column state across branches:
SELECT "BranchId", "Code", "Name", "VatDisplay", "ShowCustomerDetailsOnReceipt"
FROM "Branches"
ORDER BY "BranchId";

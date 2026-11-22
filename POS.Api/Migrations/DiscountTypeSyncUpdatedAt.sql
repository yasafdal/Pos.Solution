-- Idempotent PostgreSQL migration for DiscountTypes incremental sync timestamp and update trigger.
-- Reuses existing "DiscountTypes"."IsActive" column as the authoritative activation flag.

ALTER TABLE "DiscountTypes"
    ADD COLUMN IF NOT EXISTS "UpdatedAt" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC');

UPDATE "DiscountTypes"
SET "UpdatedAt" = COALESCE("CreatedAt", NOW() AT TIME ZONE 'UTC')
WHERE "UpdatedAt" IS NULL;

CREATE OR REPLACE FUNCTION set_discount_types_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW."UpdatedAt" = NOW() AT TIME ZONE 'UTC';
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_discount_types_updated_at ON "DiscountTypes";
CREATE TRIGGER trg_discount_types_updated_at
    BEFORE UPDATE ON "DiscountTypes"
    FOR EACH ROW
    EXECUTE FUNCTION set_discount_types_updated_at();

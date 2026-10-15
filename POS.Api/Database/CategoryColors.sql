-- Run manually only; never invoked at application startup.
-- Existing rows receive the default only when the column is first added.
-- Re-running preserves all existing colors, including valid lowercase values.
-- Existing invalid/null values cause validation to fail rather than being overwritten.
BEGIN;
SET LOCAL lock_timeout = '10s';

ALTER TABLE "Categories"
    ADD COLUMN IF NOT EXISTS "ColorHex" varchar(7) NOT NULL DEFAULT '#2563EB';
ALTER TABLE "Categories"
    ALTER COLUMN "ColorHex" SET DEFAULT '#2563EB',
    ALTER COLUMN "ColorHex" SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'CK_Categories_ColorHex'
          AND conrelid = '"Categories"'::regclass
    ) THEN
        ALTER TABLE "Categories"
            ADD CONSTRAINT "CK_Categories_ColorHex"
            CHECK (length("ColorHex") = 7 AND "ColorHex" ~ '^#[0-9A-Fa-f]{6}$');
    END IF;
END $$;

COMMIT;

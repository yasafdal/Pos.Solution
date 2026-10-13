-- Idempotent PostgreSQL script for Customer Information on Orders
-- Run manually against pos_db; never invoked at application startup.
BEGIN;
SET LOCAL lock_timeout = '10s';

DO $$
BEGIN
    IF current_database() <> 'pos_db' THEN
        RAISE EXCEPTION 'CustomerInfo.sql must run against pos_db';
    END IF;
END $$;

-- Add customer info columns to Orders table if not already present
ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CustomerName" VARCHAR(200) NULL;
ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CustomerAddress" VARCHAR(255) NULL;
ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CustomerTin" VARCHAR(50) NULL;
ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CustomerBusinessStyle" VARCHAR(100) NULL;

COMMIT;

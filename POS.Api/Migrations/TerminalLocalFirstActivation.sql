-- Migration: TerminalLocalFirstActivation
-- Adds Uuid, ActivationCode, IsActivationCodeUsed, AllowSync for local-first device pairing

ALTER TABLE "Terminals" ADD COLUMN IF NOT EXISTS "Uuid" character varying(36) NULL;
ALTER TABLE "Terminals" ALTER COLUMN "Uuid" DROP NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Terminals_Uuid" ON "Terminals" ("Uuid");

ALTER TABLE "Terminals" ADD COLUMN IF NOT EXISTS "ActivationCode" character varying(10) NULL;
CREATE INDEX IF NOT EXISTS "IX_Terminals_ActivationCode" ON "Terminals" ("ActivationCode") WHERE "ActivationCode" IS NOT NULL;

ALTER TABLE "Terminals" ADD COLUMN IF NOT EXISTS "IsActivationCodeUsed" boolean NOT NULL DEFAULT FALSE;
ALTER TABLE "Terminals" ADD COLUMN IF NOT EXISTS "AllowSync" boolean NOT NULL DEFAULT TRUE;

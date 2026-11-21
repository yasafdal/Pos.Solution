-- =============================================================================
-- POS.Solution — Separate AGRES System Admin from Client Users + Organizations
-- =============================================================================
-- Idempotent PostgreSQL migration script.
-- Preserves all existing client users (except migrating legacy 'admin' into
-- AgresSystemAdmins per approved migration plan), all PasswordHash/PinCode
-- credentials, UserIds, and historical transaction/shift/session references.
-- =============================================================================

BEGIN;

-- 1. Client Organizations (Tenants)
CREATE TABLE IF NOT EXISTS public."Organizations" (
    "OrganizationId" SERIAL PRIMARY KEY,
    "Code"           VARCHAR(30)  NOT NULL,
    "Name"           VARCHAR(120) NOT NULL,
    "IsActive"       BOOLEAN      NOT NULL DEFAULT TRUE,
    "CreatedAtUtc"   TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC')
);

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Organizations_Code"
    ON public."Organizations" ("Code");

-- Seed default OrganizationId = 1 (using existing first Branch name if available)
INSERT INTO public."Organizations" ("OrganizationId", "Code", "Name", "IsActive", "CreatedAtUtc")
SELECT
    1,
    'DEFAULT',
    COALESCE((SELECT b."Name" FROM public."Branches" b ORDER BY b."BranchId" LIMIT 1), 'Default Organization'),
    TRUE,
    NOW() AT TIME ZONE 'UTC'
WHERE NOT EXISTS (SELECT 1 FROM public."Organizations" WHERE "OrganizationId" = 1);

SELECT setval(
    pg_get_serial_sequence('public."Organizations"', 'OrganizationId'),
    GREATEST((SELECT COALESCE(MAX("OrganizationId"), 1) FROM public."Organizations"), 1)
);

-- 2. Platform-Level AGRES System Administrators (physically separate from Users; no PIN)
CREATE TABLE IF NOT EXISTS public."AgresSystemAdmins" (
    "SystemAdminId"  SERIAL PRIMARY KEY,
    "Username"       VARCHAR(50)  NOT NULL,
    "DisplayName"    VARCHAR(100) NOT NULL,
    "PasswordHash"   VARCHAR(255) NOT NULL,
    "IsActive"       BOOLEAN      NOT NULL DEFAULT TRUE,
    "CreatedAtUtc"   TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC'),
    "LastLoginAtUtc" TIMESTAMP WITHOUT TIME ZONE NULL,
    "CreatedBy"      VARCHAR(100) NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "UX_AgresSystemAdmins_Username"
    ON public."AgresSystemAdmins" ("Username");

-- 3. Platform-Level Audit Logs
CREATE TABLE IF NOT EXISTS public."PlatformAuditLogs" (
    "AuditLogId"         BIGSERIAL PRIMARY KEY,
    "ActorUsername"      VARCHAR(100) NOT NULL,
    "ActorSystemAdminId" INTEGER      NULL,
    "Action"             VARCHAR(80)  NOT NULL,
    "TargetType"         VARCHAR(80)  NOT NULL,
    "TargetId"           VARCHAR(100) NULL,
    "Details"            VARCHAR(1000) NULL,
    "IpAddress"          VARCHAR(64)  NULL,
    "CreatedAtUtc"       TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC')
);

CREATE INDEX IF NOT EXISTS "IX_PlatformAuditLogs_CreatedAtUtc"
    ON public."PlatformAuditLogs" ("CreatedAtUtc");

-- 4. Add OrganizationId + IsActive to Users, and OrganizationId to Branches
ALTER TABLE public."Users"
    ADD COLUMN IF NOT EXISTS "OrganizationId" INTEGER NOT NULL DEFAULT 1;

ALTER TABLE public."Users"
    ADD COLUMN IF NOT EXISTS "IsActive" BOOLEAN NOT NULL DEFAULT TRUE;

ALTER TABLE public."Branches"
    ADD COLUMN IF NOT EXISTS "OrganizationId" INTEGER NOT NULL DEFAULT 1;

CREATE INDEX IF NOT EXISTS "IX_Users_OrganizationId" ON public."Users" ("OrganizationId");
CREATE INDEX IF NOT EXISTS "IX_Branches_OrganizationId" ON public."Branches" ("OrganizationId");

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Users_Organizations') THEN
        ALTER TABLE public."Users"
            ADD CONSTRAINT "FK_Users_Organizations"
            FOREIGN KEY ("OrganizationId") REFERENCES public."Organizations" ("OrganizationId")
            ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Branches_Organizations') THEN
        ALTER TABLE public."Branches"
            ADD CONSTRAINT "FK_Branches_Organizations"
            FOREIGN KEY ("OrganizationId") REFERENCES public."Organizations" ("OrganizationId")
            ON DELETE RESTRICT;
    END IF;
END $$;

-- 5. Migrate legacy 'admin' account from Users -> AgresSystemAdmins (zero hardcoded credentials)
--    Preserves BCrypt PasswordHash, removes 'admin' from client Users table, keeps all other
--    client accounts (e.g. NENTINA, MREFORMO, cashiers) and all historical string references intact.
DO $$
DECLARE
    legacy_admin RECORD;
    new_admin_id INTEGER;
BEGIN
    SELECT "UserId", "Username", "PasswordHash"
    INTO legacy_admin
    FROM public."Users"
    WHERE LOWER("Username") = 'admin'
    LIMIT 1;

    IF FOUND THEN
        IF NOT EXISTS (SELECT 1 FROM public."AgresSystemAdmins" WHERE LOWER("Username") = LOWER(legacy_admin."Username")) THEN
            INSERT INTO public."AgresSystemAdmins" ("Username", "DisplayName", "PasswordHash", "IsActive", "CreatedAtUtc", "CreatedBy")
            VALUES (legacy_admin."Username", 'AGRES System Administrator', legacy_admin."PasswordHash", TRUE, NOW() AT TIME ZONE 'UTC', 'MIGRATION')
            RETURNING "SystemAdminId" INTO new_admin_id;

            INSERT INTO public."PlatformAuditLogs" ("ActorUsername", "ActorSystemAdminId", "Action", "TargetType", "TargetId", "Details", "CreatedAtUtc")
            VALUES (
                'SYSTEM_MIGRATION',
                new_admin_id,
                'MIGRATED_SYSTEM_ADMIN',
                'AgresSystemAdmin',
                new_admin_id::text,
                format('Migrated legacy UserId=%s (%s) from Users to AgresSystemAdmins and removed from client Users table.', legacy_admin."UserId", legacy_admin."Username"),
                NOW() AT TIME ZONE 'UTC'
            );
        END IF;

        DELETE FROM public."Users" WHERE "UserId" = legacy_admin."UserId";
    END IF;
END $$;

COMMIT;

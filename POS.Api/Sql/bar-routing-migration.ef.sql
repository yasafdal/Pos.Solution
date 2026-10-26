START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026070945_AddProductBarRouting') THEN
    DO $$
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Products' AND column_name = 'IsBarItem') THEN
            ALTER TABLE "Products" ADD COLUMN "IsBarItem" boolean NOT NULL DEFAULT false;
        END IF;
    END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026070945_AddProductBarRouting') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261026070945_AddProductBarRouting', '10.0.13');
    END IF;
END $EF$;
COMMIT;


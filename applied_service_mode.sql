
CREATE TABLE IF NOT EXISTS "ServiceMode" (
    "id" SERIAL PRIMARY KEY,
    "type" VARCHAR(50) NOT NULL UNIQUE,
    "description" VARCHAR(255) NOT NULL
);

INSERT INTO "ServiceMode" ("type", "description") 
VALUES 
('QUICK_SERVICE', 'Coffee Shop, Fast Food'),
('TABLE_SERVICE', 'Dine-in Restaurant'),
('MIXED', 'Restaurant with counter and dine-in service')
ON CONFLICT ("type") DO NOTHING;

DO $$ 
BEGIN 
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                   WHERE table_name='Branches' AND column_name='ServiceModeId') THEN
        ALTER TABLE "Branches" ADD COLUMN "ServiceModeId" INTEGER;
        ALTER TABLE "Branches" ADD CONSTRAINT "FK_Branches_ServiceMode" 
            FOREIGN KEY ("ServiceModeId") REFERENCES "ServiceMode" ("id") ON DELETE SET NULL;
    END IF;
END $$;

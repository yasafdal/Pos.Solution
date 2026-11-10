ALTER TABLE "OrderItems" ADD COLUMN IF NOT EXISTS "SpecialInstructions" character varying(250) NULL;
ALTER TABLE "HeldSaleItems" ADD COLUMN IF NOT EXISTS "SpecialInstructions" character varying(250) NULL;

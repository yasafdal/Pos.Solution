-- Idempotent PostgreSQL script to add ProductShortCode and OrderItems.product_short_code
DO $$ 
BEGIN 
    -- 1. Add ShortCode to Products if it doesn't exist
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_name = 'Products' AND column_name = 'ShortCode'
    ) THEN
        ALTER TABLE "Products" ADD COLUMN "ShortCode" VARCHAR(30) NULL;
    END IF;

    -- 2. Add ProductShortCode to OrderItems if it doesn't exist
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_name = 'OrderItems' AND column_name = 'ProductShortCode'
    ) THEN
        ALTER TABLE "OrderItems" ADD COLUMN "ProductShortCode" VARCHAR(30) NULL;
    END IF;
END $$;

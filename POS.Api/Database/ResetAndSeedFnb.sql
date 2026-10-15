-- Reset and Seed FNB Quick Service with Raw Materials & Recipes
BEGIN;
SET LOCAL lock_timeout = '10s';

-- Clear dependent tables first to avoid FK violations
DELETE FROM "StockAdjustments";
DELETE FROM "ProductRecipes";
DELETE FROM "BranchInventory";
DELETE FROM "Products";
DELETE FROM "Categories";

-- Drop StockQuantity and ReorderLevel columns from Products if they exist
ALTER TABLE "Products" DROP COLUMN IF EXISTS "StockQuantity";
ALTER TABLE "Products" DROP COLUMN IF EXISTS "ReorderLevel";
ALTER TABLE "Products" DROP COLUMN IF EXISTS "stockquantity";
ALTER TABLE "Products" DROP COLUMN IF EXISTS "reorderlevel";

-- Seed Categories for Coffee Shop & Small Food Business
INSERT INTO "Categories" ("CategoryId", "Name", "Description", "IsActive", "ColorHex")
VALUES
(1, 'Coffee & Espresso', 'Hot & iced artisanal coffee drinks', true, '#2563EB'),
(2, 'Teas & Beverages', 'Milk teas, fruit teas, and refreshing drinks', true, '#16A34A'),
(3, 'Bakery & Pastries', 'Freshly baked muffins, pastries, and breads', true, '#D97706'),
(4, 'Rice Meals & Sandwiches', 'Hearty meals and quick snacks', true, '#DC2626'),
(5, 'Add-ons & Extras', 'Extra shots, syrups, and toppings', true, '#9333EA'),
(6, 'Inventory & Raw Materials', 'Ingredients and raw supplies', true, '#475569')
ON CONFLICT ("CategoryId") DO UPDATE 
SET "Name" = EXCLUDED."Name", "Description" = EXCLUDED."Description", "ColorHex" = COALESCE(EXCLUDED."ColorHex", '#2563EB');

-- Reset sequence for Categories if needed
SELECT setval(pg_get_serial_sequence('"Categories"', 'CategoryId'), coalesce(max("CategoryId"), 1), true) FROM "Categories";

-- Seed Raw Materials first (CategoryId = 6)
INSERT INTO "Products" ("ProductId", "CategoryId", "Name", "Price", "CostPrice", "ProductType", "UnitOfMeasure", "Barcode", "SKU", "IsActive", "IsKitchenItem")
OVERRIDING SYSTEM VALUE VALUES
(101, 6, 'Coffee Beans (Arabica 250g)', 350.00, 200.00, 'RawMaterial', 'pack', '480000101', 'MAT-BEA-250', true, false),
(102, 6, 'Fresh Milk 1L', 95.00, 70.00, 'RawMaterial', 'bottle', '480000102', 'MAT-MLK-1L', true, false),
(103, 6, 'Matcha Powder 100g', 280.00, 180.00, 'RawMaterial', 'pack', '480000103', 'MAT-MAT-100', true, false),
(104, 6, 'Black Tea Bags (Box)', 150.00, 100.00, 'RawMaterial', 'box', '480000104', 'MAT-TEA-BOX', true, false),
(105, 6, 'White Rice (1kg)', 60.00, 45.00, 'RawMaterial', 'kg', '480000105', 'MAT-RIC-1KG', true, false),
(106, 6, 'Beef Tapa (1kg)', 450.00, 320.00, 'RawMaterial', 'kg', '480000106', 'MAT-TAP-1KG', true, false),
(107, 6, 'Fresh Eggs (Tray)', 220.00, 180.00, 'RawMaterial', 'tray', '480000107', 'MAT-EGG-TRY', true, false),
(108, 6, 'Sliced Bread (Loaf)', 75.00, 50.00, 'RawMaterial', 'loaf', '480000108', 'MAT-BRD-LOF', true, false),
(109, 6, 'Cheddar Cheese (Block)', 210.00, 150.00, 'RawMaterial', 'block', '480000109', 'MAT-CHE-BLK', true, false),
(110, 6, 'Caramel Syrup Bottle', 320.00, 220.00, 'RawMaterial', 'bottle', '480000110', 'MAT-SYR-BOT', true, false)
ON CONFLICT ("ProductId") DO NOTHING;

-- Seed Prepared / Finished Menu Products (MadeToOrder & Standard)
INSERT INTO "Products" ("ProductId", "CategoryId", "Name", "Price", "CostPrice", "ProductType", "UnitOfMeasure", "Barcode", "SKU", "IsActive", "IsKitchenItem")
OVERRIDING SYSTEM VALUE VALUES
-- Coffee & Espresso (CategoryId = 1) -> MadeToOrder
(1, 1, 'Iced Cafe Latte', 140.00, 45.00, 'MadeToOrder', 'cup', '480001001', 'COF-LAT-ICD', true, true),
(2, 1, 'Hot Cappuccino', 120.00, 35.00, 'MadeToOrder', 'cup', '480001002', 'COF-CAP-HOT', true, true),
(3, 1, 'Custom Espresso Shot', 40.00, 10.00, 'MadeToOrder', 'shot', '480001003', 'COF-ESP-CUS', true, true),

-- Teas & Beverages (CategoryId = 2) -> MadeToOrder
(4, 2, 'Matcha Milk Tea', 160.00, 50.00, 'MadeToOrder', 'cup', '480002001', 'BEV-MAT-MIL', true, true),
(5, 2, 'Lemon Fruit Tea', 110.00, 30.00, 'MadeToOrder', 'cup', '480002002', 'BEV-LEM-TEA', true, true),

-- Bakery & Pastries (CategoryId = 3) -> Standard (pre-baked)
(6, 3, 'Blueberry Muffin', 95.00, 30.00, 'Standard', 'piece', '480003001', 'BAK-BLU-MUF', true, false),
(7, 3, 'Butter Croissant', 85.00, 25.00, 'Standard', 'piece', '480003002', 'BAK-BUT-CRO', true, false),

-- Rice Meals & Sandwiches (CategoryId = 4) -> MadeToOrder
(8, 4, 'Tapsilog Meal', 180.00, 75.00, 'MadeToOrder', 'plate', '480004001', 'MEAL-TAP-01', true, true),
(9, 4, 'Ham & Cheese Sandwich', 130.00, 45.00, 'MadeToOrder', 'pc', '480004002', 'SND-HAM-01', true, true),

-- Add-ons & Extras (CategoryId = 5) -> Standard
(10, 5, 'Extra Pearls / Sinkers', 20.00, 5.00, 'Standard', 'scoop', '480005001', 'ADD-PRL-01', true, false),
(11, 5, 'Caramel Syrup Pump', 25.00, 8.00, 'Standard', 'pump', '480005002', 'ADD-CAR-01', true, false)
ON CONFLICT ("ProductId") DO NOTHING;

-- Reset sequence for Products
SELECT setval(pg_get_serial_sequence('"Products"', 'ProductId'), coalesce(max("ProductId"), 1), true) FROM "Products";

-- Seed Product Recipes (Linking MadeToOrder items to Raw Materials)
INSERT INTO "ProductRecipes" ("ProductId", "RawMaterialProductId", "QuantityRequired", "Uom")
VALUES
-- Iced Cafe Latte (ProductId = 1) -> Coffee Beans (101), Fresh Milk (102), Caramel Syrup (110)
(1, 101, 0.05, 'pack'),
(1, 102, 0.20, 'bottle'),
(1, 110, 0.05, 'bottle'),

-- Hot Cappuccino (ProductId = 2) -> Coffee Beans (101), Fresh Milk (102)
(2, 101, 0.05, 'pack'),
(2, 102, 0.15, 'bottle'),

-- Custom Espresso Shot (ProductId = 3) -> Coffee Beans (101)
(3, 101, 0.04, 'pack'),

-- Matcha Milk Tea (ProductId = 4) -> Matcha Powder (103), Fresh Milk (102)
(4, 103, 0.10, 'pack'),
(4, 102, 0.15, 'bottle'),

-- Lemon Fruit Tea (ProductId = 5) -> Black Tea Bags (104)
(5, 104, 0.10, 'box'),

-- Tapsilog Meal (ProductId = 8) -> White Rice (105), Beef Tapa (106), Fresh Eggs (107)
(8, 105, 0.25, 'kg'),
(8, 106, 0.20, 'kg'),
(8, 107, 0.05, 'tray'),

-- Ham & Cheese Sandwich (ProductId = 9) -> Sliced Bread (108), Cheddar Cheese (109)
(9, 108, 0.20, 'loaf'),
(9, 109, 0.05, 'block')
ON CONFLICT DO NOTHING;

COMMIT;

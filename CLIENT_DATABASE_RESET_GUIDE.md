# POS.SOLUTION — CLIENT DATABASE RESET & INITIALIZATION GUIDE

This guide provides the complete procedure for clearing transactional data and client-specific setup from a PostgreSQL database (`pos_db`) and local SQLite terminal databases (`local_pos.db`) when onboarding a new POS client.

---

## 1. Scope & Table Inventory

The cleanup strictly isolates business and transactional records while preserving core system engines, database schemas, and master lookup tables.

### A. Tables Cleared Completely (Transactional & Operational Data)

| Category | PostgreSQL Table | Description |
| :--- | :--- | :--- |
| **Sales & Payments** | `public."Orders"` | Sales transactions, invoices, customer references |
| | `public."OrderItems"` | Order line items, prices, tax classifications |
| | `public."OrderItemModifiers"` | Selected modifiers on order items |
| | `public."Payments"` | Cash, GCash, Card tender records |
| **Voids & Returns** | `public."TransactionVoids"` | Voided orders and supervisor approval records |
| | `public."SalesReturns"` | Returns header and return references |
| | `public."SalesReturnItems"` | Returned item details and restock disposition |
| | `public."Refunds"` | Payment reversals and cash return details |
| **Cash Drawer & Shifts**| `public."CashDrawerShifts"` | Cashier opening floats, expected balances, X-readings |
| | `public."CashDrawerMovements"` | Petty cash, cash-in, cash-out, safe drops |
| | `public."CashierSessions"` | Cashier login and logout history |
| | `public."CashierSwitchLogs"` | Mid-shift cashier changeover logs |
| **Parked / Held Sales** | `public."HeldSales"` | Cart parking headers and notes |
| | `public."HeldSaleItems"` | Items in parked carts |
| | `public."HeldSaleItemModifiers"`| Modifiers on parked cart items |
| | `public."HeldSaleAuditLogs"` | Audit log of hold and resume events |
| **Audits & Sync Queues**| `public."ReceiptReprintLogs"` | Controlled receipt reprint audit trail |
| | `public."DiscountAuditLogs"` | Senior Citizen / PWD discount authorization trail |
| | `public."TerminalRecoveryLogs"`| Supervisor historical transaction recovery logs |
| | `public."OfflineSyncIdempotency"`| Deduplication records for replayed offline syncs |
| **Fiscal Z-Readings** | `public."ZReadings"` | Finalized daily Z-reading snapshots |
| | `public."ZReadingPrintAttempts"`| Z-reading physical print attempt logs |
| | `public."ZReadingPrintResults"` | Z-reading printer result logs |
| | `public."ZReadingTerminalStates"`| Baseline accumulated sales and Z-number state |
| **Inventory Movements** | `public."StockAdjustments"` | Inventory adjustment history and sync stock logs |

### B. Tables Cleared (Product & Catalog Setup)

| Category | PostgreSQL Table | Description |
| :--- | :--- | :--- |
| **Catalog & Menu** | `public."BranchInventory"` | Branch stock balances and reorder levels |
| | `public."ProductRecipes"` | Bill of Materials (BOM) / raw ingredient links |
| | `public."ProductModifierGroup"` | Junction linking products to modifier groups |
| | `public."ModifierOption"` | Specific modifier items and price increments |
| | `public."ModifierGroup"` | Modifier groups (e.g., Sugar Level, Add-ons) |
| | `public."Products"` | Product catalog, SKUs, barcodes, kitchen/bar flags |
| | `public."Categories"` | Product category classifications |

### C. Tables Reset or Managed for New Client Setup

| Table | Reset Action | Reason |
| :--- | :--- | :--- |
| `public."InvoiceSequences"` | **Cleared / Recreated** | Old series belongs to previous client's BIR PTU |
| `public."OrderSequences"` | **Cleared / Recreated** | Daily order number sequences belong to previous terminal |
| `public."Terminals"` | **Preserved / Reset** | Uuid, activation code, and closing locks cleared |
| `public."Outlets"` | **Preserved / Reset** | Opening cash fund cleared; code/name updated in Web |
| `public."Branches"` | **Preserved** | Address, TIN, and Name updated for new client in Web |
| `public."Users"` | **Filtered** | Non-Admin staff cleared; Admin/Manager accounts kept |

### D. System Tables Strictly Preserved (Never Modified)

| PostgreSQL Table | Description |
| :--- | :--- |
| `public."BusinessType"` | System reference types (Quick Service, Retail, etc.) |
| `public."ServiceMode"` | System service modes (Dine-In, Take-Out, Delivery) |
| `public."PaymentMethods"` | Global payment method configurations (CASH, GCASH, CARD) |
| `public."Taxes"` | System tax rates (VAT 12%, Non-VAT, VAT-Exempt) |
| `public."DiscountTypes"` | Statutory discount definitions (Senior Citizen 20%, PWD 20%) |
| `public."CashMovementCategories"`| Predefined petty cash & cash fund movement categories |
| `public."UnitOfMeasures"` | Standard units of measure (PCS, KG, L, G, ML) |
| `public."__EFMigrationsHistory"` | Entity Framework Core database schema migration tracking |

---

## 2. Pre-Execution Safety: Database Backup

Always generate a physical backup before running any cleanup script.

### PostgreSQL Backup Command:
```bash
pg_dump -h localhost -p 5432 -U postgres -d pos_db -F c -b -v -f "pos_db_pre_reset_backup_$(date +%Y%m%d_%H%M%S).dump"
```

### Quick Verification of Backup:
```bash
pg_restore -l "pos_db_pre_reset_backup_*.dump" | head -n 20
```

---

## 3. PostgreSQL Database Reset Instructions

The script `postgres_client_reset.sql` is structured with safeguards:

1. **Safety Block on Critical DB Names**: The script aborts if the database name contains `prod`, `production`, or `master_pos`.
2. **Transactional Dry-Run by Default**: Ends with `ROLLBACK;` so executing it initially changes nothing.
3. **Trigger Suspension**: Disables user triggers (such as Z-reading immutability guards) during table truncation, then immediately re-enables them.

### Step 3.1: Run in Dry-Run Mode
Execute the script as-is:
```bash
psql -h localhost -U postgres -d pos_db -f postgres_client_reset.sql
```
- Observe the console output.
- Check the `PRE-CLEANUP ROW COUNT PREVIEW` and `POST-CLEANUP VERIFICATION`.
- Confirm `ROLLBACK` executes successfully at the end.

### Step 3.2: Commit the Cleanup
Once verified:
1. Open [`postgres_client_reset.sql`](file:///c:/Users/yaser/source/repos/Pos.Solution/postgres_client_reset.sql).
2. Go to the last line.
3. Change:
   ```sql
   ROLLBACK;
   -- COMMIT;
   ```
   to:
   ```sql
   -- ROLLBACK;
   COMMIT;
   ```
4. Re-run:
   ```bash
   psql -h localhost -U postgres -d pos_db -f postgres_client_reset.sql
   ```

---

## 4. Local POS Terminal (SQLite) Reset Instructions

Deleting database records on the server **does not** automatically wipe offline SQLite databases on deployed terminals. Terminals previously used in testing or for Client 1 must be reset.

### Method 1: Delete Local SQLite File (Recommended & Simplest)
1. Close the `POS.Client` application on the terminal device.
2. Delete the local database file:
   - **Windows**:
     ```powershell
     Remove-Item "$env:LOCALAPPDATA\PosSolution\local_pos.db*" -Force
     ```
   - **Android / Other**:
     Clear application data via device Application Settings or delete `/data/user/0/.../files/PosSolution/local_pos.db`.
3. Launch `POS.Client`.
4. The application automatically builds the schema, recognizes that it is unactivated, and presents the **Device Activation Screen**.

### Method 2: Execute `sqlite_client_reset.sql`
If retaining the existing file is required:
```powershell
sqlite3 "$env:LOCALAPPDATA\PosSolution\local_pos.db" < sqlite_client_reset.sql
```

---

## 5. Regulatory & BIR Numbering Setup Guidelines

> [!IMPORTANT]
> Do not generate random invoice sequences or arbitrary fiscal totals. All settings below must align with the second client's official BIR Permit to Use (PTU).

### A. Invoice Sequence Configuration
In the Philippines, every POS terminal issuing official sales invoices must be configured with its BIR-registered series:
- **Series Code**: Unique series identifier (e.g. `SI2026-T01`).
- **Prefix**: Registered invoice prefix (e.g. `SI-` or `INV-`).
- **Starting Number**: Authorized start number from the BIR Notice to Issue Receipts / Invoices (typically `1`).
- **Ending Number**: Upper limit of the approved series range.
- **Last Issued Number**: Must be set to `StartingNumber - 1` (e.g., `0` if starting number is `1`).
- **Padding Length**: Number of digits (typically `6` or `7`, e.g. `0000001`).

### B. Z-Reading Terminal Baseline
When initializing a terminal for a new client:
- **`OpeningAccumulatedSales`**: Initial cumulative gross sales. Must be `0.00` for a brand new machine, or the certified turnover balance from the previous system.
- **`LastZNumber`**: Set to `0`. The first daily Z-reading will increment this to `1`.
- **`ResetCounter`**: Set to `0` or incremented in accordance with the machine's BIR registration record.

---

## 6. Post-Cleanup Validation Queries

Run these queries in PostgreSQL to verify that the database is in a clean, consistent state:

```sql
-- 1. Check that all transactional tables are completely empty (Expect 0)
SELECT
    (SELECT count(*) FROM public."Orders") AS orders_count,
    (SELECT count(*) FROM public."Payments") AS payments_count,
    (SELECT count(*) FROM public."CashDrawerShifts") AS shifts_count,
    (SELECT count(*) FROM public."CashDrawerMovements") AS movements_count,
    (SELECT count(*) FROM public."HeldSales") AS held_sales_count,
    (SELECT count(*) FROM public."ZReadings") AS z_readings_count,
    (SELECT count(*) FROM public."Products") AS products_count;

-- 2. Check that system lookups and master tables are intact (Expect > 0)
SELECT
    (SELECT count(*) FROM public."PaymentMethods") AS payment_methods_count,
    (SELECT count(*) FROM public."Taxes") AS taxes_count,
    (SELECT count(*) FROM public."DiscountTypes") AS discount_types_count,
    (SELECT count(*) FROM public."BusinessType") AS business_types_count,
    (SELECT count(*) FROM public."ServiceMode") AS service_modes_count,
    (SELECT count(*) FROM public."CashMovementCategories") AS cash_movement_cats_count,
    (SELECT count(*) FROM public."Users" WHERE "Role" = 'Admin') AS admin_users_count;

-- 3. Check that Terminals are unlocked and ready for activation
SELECT "TerminalId", "TerminalCode", "Name", "Uuid", "ActivationCode", "ClosedBusinessDate"
FROM public."Terminals";
```

---

## 7. New Client Onboarding Checklist

Follow this setup sequence in `POS.Web` and on the terminal to complete onboarding:

- [ ] **Step 1: Configure Branch**
  - Update `Branches`: Set Company / Business Name, Registered Address, TIN, Phone Number, and Tax Type (`VAT-Registered` or `Non-VAT`).
- [ ] **Step 2: Configure Outlet**
  - Set up selling outlets (e.g., Main Dining, Bar, Drive-thru).
  - Configure default opening cash fund if applicable.
- [ ] **Step 3: Register Terminal**
  - Configure Terminal Code, Name, IP address, Serial Number, Machine Identification Number (MIN), and Permit to Use (PTU) Number.
  - Generate a new 6-digit **Terminal Activation Code**.
- [ ] **Step 4: Configure Invoice Numbering Sequences**
  - Go to Terminal Configuration > Invoice Sequences.
  - Add active series with BIR approved Prefix, Starting Number, and Ending Number.
- [ ] **Step 5: Create Cashier & Manager Accounts**
  - In `POS.Web`, create staff accounts with secure passwords and 4-6 digit numeric PIN codes.
- [ ] **Step 6: Configure Tax Settings**
  - Confirm VAT rate (12%) or Exempt status matches client's registration.
- [ ] **Step 7: Configure Payment Methods**
  - Verify active payment tenders (Cash, GCash, Maya, Credit/Debit Card).
- [ ] **Step 8: Configure Product Categories**
  - Create client product categories and assign display colors.
- [ ] **Step 9: Create Products & Menu Items**
  - Add products with SKU, barcode, unit price, tax type, and kitchen/bar routing flags.
  - Create modifier groups and options if items have customizations.
- [ ] **Step 10: Configure Initial Inventory Balances**
  - Set stock quantities and reorder thresholds in Branch Inventory.
- [ ] **Step 11: Configure Kitchen & Bar Routing**
  - Set network/USB printer assignments for food and drink tickets.
- [ ] **Step 12: Initialize Local POS Terminal**
  - Delete `local_pos.db` on terminal and launch `POS.Client`.
  - Enter the Terminal Activation Code generated in Step 3.
- [ ] **Step 13: Perform Initial Master Data Synchronization**
  - Log in with cashier PIN.
  - Verify all products, categories, taxes, and payment methods sync cleanly.
- [ ] **Step 14: Confirm Initial Cash Fund & New Sales**
  - Open cashier shift and confirm initial cash fund count.
  - Process test cash sale and non-cash sale.
  - Verify receipt printing and sequence advancement (`0000001`).
- [ ] **Step 15: Verify Shift Close & X/Z-Reading**
  - Close cashier shift.
  - Generate X-Reading and perform daily Z-Reading.
  - Confirm electronic journal audit records match expectations.

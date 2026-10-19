---
title: "Pos.Solution Production Readiness & Architecture Remediation Map"
labels:
  - wayfinder:map
---

## Destination

Bring **Pos.Solution** (`POS.Api`, `POS.Client`, `POS.Web`, `POS.Shared`) and its PostgreSQL/SQLite database layers to a secure, compile-clean, BIR-compliant, and transactionally consistent production state based on the architectural review in [`project_content.md`](../project_content.md).

## Notes

- **Domain:** Multi-branch Retail & F&B Point of Sale (POS) with Philippine BIR Tax/EOPT Compliance (`docs/BIR_RESEARCH.md`), Offline-First MAUI Blazor Hybrid Client (`POS.Client`), Blazor Server Back-Office (`POS.Web`), and ASP.NET Core 10 / PostgreSQL API (`POS.Api`).
- **Canonical Architecture Reference:** [`project_content.md`](../project_content.md)
- **Skills to consult per session:** `wayfinder`, `investigate-first`, `surgical-patch`, `safe-refactor`, `migration`, `verify-and-stop`.
- **Tracker Mode:** Local Markdown tracker (`.plan/MAP.md` and `.plan/tickets/`).

### Frontier Tickets (Open & Unclaimed)
*(All Track 1 offline payment processing tickets completed; Track 2–5 items below are ready to be ticketed next.)*

### Additional Roadmap Tracks
1. **Track 2 — Security & Access Control Hardening (`wayfinder:grilling`):**
   - **Fix Supervisor Approval Bypass & Anonymous Void/Return Endpoints:** Remove `[AllowAnonymous]` from `ReturnsController` and `TransactionsController.VoidOrder`, require cryptographic/PIN verification (`u.PinCode == cleanPin` only, never `u.Username == cleanPin`), and hash/enforce uniqueness on user PINs.
   - **Enforce `[Authorize]` & Role Policies Across All API Controllers:** Lock down the 18+ unauthenticated controllers, stop returning plaintext `PinCode` in `GET api/Users`, and fix the cashier-to-supervisor Bearer token overwrite in `POS.Client/Components/Pages/Transactions.razor`.
   - **Externalize Secrets & Remove Production TLS Bypass:** Move hardcoded PostgreSQL/JWT secrets out of `PosDbContext.cs`, `MigrationRunner.cs`, and `appsettings.json`, and restrict `ServerCertificateCustomValidationCallback` in `POS.Web/Program.cs` to `IsDevelopment()`.
2. **Track 3 — Database Schema, Precision & Migration Unification (`wayfinder:grilling`):**
   - **Unify Schema Migrations & Retire Broken `MigrationRunner.cs`:** Replace missing `Database/*.sql` execution with a single authoritative EF Core migration / idempotent SQL baseline, and remove the unused SQL Server `ApplicationDbContext` from `POS.Web`.
   - **Fix Decimal Precision & Inventory Quantity Types:** Fix `Taxis.Rate` (`.HasPrecision(18, 4)`), add explicit `.HasPrecision(18, 2)` / `(5, 2)` to `Order` and `OrderItem` VAT/discount columns, register `DiscountType` and `DiscountAuditLog` in `PosDbContext.Extensions.cs`, and decide whether `BranchInventory.StockQuantity` and `StockAdjustment` quantities migrate from `int` to `numeric(18,4)` to support fractional `ProductRecipe` BOM and weighted items.
3. **Track 4 — Financial, Inventory & Online Checkout Integrity (`wayfinder:grilling`):**
   - **Implement Online Inventory & Recipe BOM Deduction:** Deduct `BranchInventory` (and `ProductRecipes` raw materials for `MadeToOrder` items) with `FOR UPDATE` locking and `StockAdjustment` audit logging during online checkout and void/return reversals.
   - **Wire Online Payments & Refunds into `CashDrawerShifts`:** Update `TotalCashSales`, `TotalCashRefunds`, `ExpectedBalance`, and `CashDrawerMovements` during online `PaymentsController` and `ReturnsController` execution.
   - **Fix Order Pricing, Duplicate-Product Line Matching & Return Refund Math:** Look up authoritative product/modifier prices on the server in `OrdersController`, match calculated line items by line index instead of `ProductId`, store net vs gross line totals clearly so `ReturnsController` refunds the actual net discounted price paid, and connect `DiscountCalculationService` to database-configured `DiscountTypes`.
4. **Track 5 — Reporting Accuracy & Web Admin Polish (`wayfinder:task`):**
   - **Fix Sales Report & Transaction Summary Filters and Net Calculations:** Pass `DateFrom` in `SalesReportApiService`, apply `dateFrom` in `TransactionsController.GetTransactionSummary`, subtract `SalesReturns`/`VOIDED` orders in `SalesReportService`, and fix split-payment method attribution.
   - **Fix Web Admin Error Handling & UI Bindings:** Enforce `EnsureSuccessStatusCode()` in `ProductService`/`CategoryService`, remove the destructive `MaxBy(p => p.ProductId)` fallback in `ManageProducts.razor`, and wire `currentBranch.TrackInventory` in `ManageBranches.razor`.

## Decisions so far

<!-- the index: one line per closed ticket, enough to judge relevance, then zoom the link for the detail the ticket holds -->

- **[001 — Debug and Fix Offline POS Payment Processing Pipeline Without Breaking Inventory Deduction](tickets/001-fix-offline-pos-payment-processing.md)**: Isolated `LocalPosDbContext` instances (`Transient` + `LocalPosDbContext.WriteLock`), synced and auto-allocated `LocalInvoiceSequences` inside the atomic SQLite checkout transaction after validating `decimal` payment totals, fixed detached `SyncOutbox` status updates, and hardened `OfflineSyncController.SyncTransaction` idempotency (`OfflineSyncIdempotencyRecords`) while preserving atomic `BranchInventories` + `StockAdjustments` (`OFFLINE_SALE_SYNC`) deductions.
- **[002 — Enable Offline Held Sales and Safe Offline Cashier Switching](tickets/002-offline-held-sales-and-cashier-switch.md)**: Both Held Sales and Switch Cashier are safe in offline mode when backed by SQLite: added `LocalHeldSales` + `ClientHeldSaleService` for offline cart parking/resuming/cancelling, and `LocalCashiers` (with SHA-256 `PinHash` values, never plaintext PINs) + `LocalOutletConfigs` fallback in `Login.razor` and `MainLayout.razor` so cashiers can switch shifts offline without terminal lockout.
- **[003 — Implement Petty Cash & Cash Movement Management Across API, Client, Web & Offline Sync](tickets/003-petty-cash-and-cash-movement-management.md)**: Extended `CashDrawerShifts` and `CashDrawerMovements` + added `CashMovementCategories` across PostgreSQL and SQLite; implemented `CashDrawerController` (`paid-in`, `paid-out`, `approve`, `reject`, `reverse`, `categories`, `shifts/{id}/totals`, `reports/summary`) with PIN-only supervisor verification, wired online `CASH` sales/refunds into open shifts, built `POS.Client` `CashDrawer.razor` with offline sync (`POST api/offline-sync/cash-movements`) + printable vouchers, added `POS.Web` `CashMovementsManagement.razor`, and verified all 18 Layer-10 test scenarios in `POS.Tests`.
- **[004 — Remove Hardcoded Printer Name and Configuration from Receipt Printing Pipeline](tickets/004-printer-agnostic-receipt-printing.md)**: Receipt printing now uses OS default printer (`core.PrintAsync(null)`) with print-dialog fallback; no hardcoded queue name or media size.
- **[005 — Scope POS.Client Transaction History to Today, This Terminal's Branch and Outlet](tickets/005-transactions-today-terminal-scope.md)**: Client forces branch + outlet + today (local midnight) on every load; API caps cashiers to own transactions within 24h, enforces ownership on detail, applies `dateFrom` to summary, normalises UTC dates.

## Not yet specified

<!-- see "Fog of war": in-scope fog you can't ticket yet; graduates as the frontier advances -->

- **Inventory deduction timing & negative-stock policy:** Should online stock and recipe BOM deductions occur at `OrdersController.CreateOrder` (`PENDING`) or `PaymentsController.ProcessPayment` (`PAID`), and should insufficient stock block checkout or allow negative on-hand balances with supervisor alert?
- **Offline BIR Invoice Series strategy:** Should offline terminals share the outlet's active `InvoiceSequence` (advancing `LastIssuedNumber` on sync) or use a dedicated offline BIR series to prevent online/offline invoice number collisions while offline?
- **BIR Z-Reading & EOPT Creditable Withholding Tax (RR No. 6-2026):** How should Z-read counter persistence and Form 2306/2307 CWT line items (`docs/BIR_RESEARCH.md` Section 4 & 5) be modeled on `Order` and `ReceiptDto` once core tax/discount defects are resolved?
- **Automated Test Suite Restoration:** Which integration and compliance test suites (previously in `POS.Tests`) should be re-established as regression gates for each remediation track?

## Out of scope

<!-- see "Out of scope": work ruled beyond the destination; closed, never graduates -->

- **Third-party payment gateway hardware SDK integrations:** Live EMV terminal / GCash / Maya webhook integrations remain out of scope until core payment state and reference number flows are stabilized.
- **External ERP / Accounting GL export:** Automated general ledger export beyond POS sales/tax reporting and BIR audit logs.

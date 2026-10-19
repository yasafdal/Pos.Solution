---
id: "001"
title: "Debug and Fix Offline POS Payment Processing Pipeline Without Breaking Inventory Deduction"
parent: "../MAP.md"
labels:
  - wayfinder:task
status: closed
assignee: antigravity
---

## Question

How do we resolve all compile-time, local SQLite transaction, invoice sequence synchronization, and server outbox sync failures preventing the offline POS (`POS.Client` -> `POS.Api`) from processing payments, while strictly preserving and hardening the inventory deduction (`BranchInventories` + `StockAdjustments`) in `OfflineSyncController`?

---

## Traced Payment Pipeline & Root Causes

### Stage 1 — UI Checkout Entry & Cart State (`POS.Client/Components/Pages/NewSale.razor`, `CartService.cs`, `DiscountModal.razor`)
1. **Undeclared `isOffline` & Missing DTO/Service Members (`NewSale.razor:1226-1237, 1337`):**
   - `ProceedToPayment()` (`L1226`) and `ConfirmPayment()` (`L1337`) check `if (isOffline)`, which is never declared in `NewSale.razor` (`LoadCatalogAsync` at `L683` and `AddToCart` at `L851` store `await ConnectivityService.CheckConnectivityAsync()` in a local variable only).
   - `ProceedToPayment()` (`L1235`) reads `Cart.TotalAmount` (does not exist on `CartService`; `CartService` exposes `Subtotal`, `Total => Subtotal`, and `ActiveDiscountAmount`).
   - `ProceedToPayment()` (`L1236`) sets `OrderDate = DateTime.UtcNow` on `OrderResponseDto` (property is `CreatedAt` in `OrderResponseDto.cs:32`).
   - `LocalPosModels.cs:1` imports `using Microsoft.Build.Tasks.Deployment.Bootstrapper;`, breaking MAUI compilation.
2. **Offline Discount Calculation Fails (`DiscountModal.razor:151`):**
   - `ConfirmDiscount()` calls `await DiscountService.CalculateOrderAsync(req)` (`POST api/Discounts/calculate`) with no fallback to `LocalDiscountCalculationService` when offline.
3. **Offline Catalog Projection Drops `TaxName`, `Barcode` & `ShortCode` (`NewSale.razor:703-705`):**
   - When offline, `LoadCatalogAsync()` projects `LocalDb.Products` into `ProductDto` without copying `TaxName`, `Barcode`, `ShortCode`, `UnitOfMeasure`, or `ImageUrl`.

### Stage 2 — Reference Data Sync & Missing Local Invoice Sequence (`OfflineReferenceDataController.cs`, `ReferenceDataSyncService.cs`)
4. **`LocalInvoiceSequence` Is Never Synced (`OfflineReferenceDataController.cs:124-143`, `ReferenceDataSyncService.cs:27-179`):**
   - `OfflineReferenceDataResponseDto` has no `InvoiceSequences` collection, and `ReferenceDataSyncService.SyncReferenceDataAsync` never writes to `_localDb.InvoiceSequences`.
   - Consequently, `OfflineCheckoutService.ProcessOfflineCashSaleAsync` (`OfflineCheckoutService.cs:65-71`) queries `_localDb.InvoiceSequences.FirstOrDefaultAsync(s => s.OutletId == createDto.OutletId && s.IsActive)`, gets `null`, and **always aborts payment with `"Offline invoice sequence exhausted."`**
5. **Nullable `p.CostPrice.Value` Crash Abort (`OfflineReferenceDataController.cs:51`):**
   - `CostPrice = p.CostPrice.Value` throws `InvalidOperationException` (HTTP 500) if any product has `NULL` `CostPrice`, aborting the entire reference data sync on login.

### Stage 3 — Local SQLite Transactions & Singleton `DbContext` Locks (`OfflineCheckoutService.cs`, `LocalPosDbContext.cs`, `MauiProgram.cs`)
6. **Singleton `LocalPosDbContext` Concurrent Access & Lock Contention (`MauiProgram.cs:39`, `MainLayout.razor:77-85`, `Home.razor:23-39`, `OfflineBackgroundSyncManager.cs:75`):**
   - `LocalPosDbContext` is registered as a **Singleton**, shared across the UI thread (`NewSale.razor`, `OfflineCheckoutService`), two 15-second `System.Threading.Timer` callbacks (`MainLayout.razor`, `Home.razor`), and the 30-second `OfflineBackgroundSyncManager` worker.
   - Concurrent operations on the same `DbContext` instance throw `InvalidOperationException: A second operation was started on this context instance before a previous operation completed` or `SqliteException: SQLite Error 5: 'database is locked'` during `OfflineCheckoutService.ProcessOfflineCashSaleAsync` (`L61-179`).
   - Additionally, `MainLayout.razor:69` and `NewSale.razor:698, 862` repeatedly execute `LocalDb.InitializeDatabase()` (`EnsureCreated()` + `PRAGMA` statements) on the shared connection.
7. **Unawaited Transaction Rollback on Early Return (`OfflineCheckoutService.cs:61-71, 104-107`):**
   - `ProcessOfflineCashSaleAsync` opens `using var transaction = await _localDb.Database.BeginTransactionAsync();` at `L61`, then returns early at `L70` (`sequence == null`) and `L106` (`cashTendered < totalAmount`) without explicitly calling `await transaction.RollbackAsync()`.
8. **Duplicate `ProductId` Line Total Misattribution & Missing Offline Modifiers (`OfflineCheckoutService.cs:139-151`):**
   - Matches calculated items via `calcRes.CalculatedItems.FirstOrDefault(ci => ci.ProductId == itemDto.ProductId)` instead of positional index, and drops item modifiers.

### Stage 4 — Outbox Upload & Server Transaction Rollback Risks (`OfflineTransactionSyncService.cs`, `OfflineSyncController.cs`)
9. **Detached `AsNoTracking()` Outbox Mutation (`OfflineTransactionSyncService.cs:32, 48-50, 111-112`):**
   - `GetPendingOutboxItemsAsync()` queries `SyncOutboxes.AsNoTracking()`, so `outboxItem.Status = "UPLOADING"; await _localDb.SaveChangesAsync();` (`L111-112`) silently fails to persist status changes.
10. **Server Transaction Rollback Risks in `OfflineSyncController.SyncTransaction` (`OfflineSyncController.cs:31-178`):**
    - **Inventory Deduction Location (`L107-129`):** `OfflineSyncController` is where `BranchInventories.StockQuantity` is decremented (`Math.Max(0, prevQty - itemDto.Quantity)`) and `StockAdjustments` (`AdjustmentType = "OFFLINE_SALE_SYNC"`) are recorded inside `using var transaction = await context.Database.BeginTransactionAsync();` (`L48`).
    - **Rollback Risk 1 — `OrderNumber` Unique Constraint Collision (`L64, 89`):** `OFF-{yyMMdd}-{Random(1000..9999)}` has only 9,000 values per day and can collide on `Orders_OrderNumber_key`, rolling back the entire transaction including the inventory deduction.
    - **Rollback Risk 2 — `InvoiceNumber` Collision Between Online & Offline Sequences (`L31-33, 75, 89`):** Because offline checkout uses the same `InvoiceSequence` prefix/numbers without advancing server `InvoiceSequences.LastIssuedNumber` (or using an offline prefix), an online sale and an offline sale can generate the same `InvoiceNumber`. Either the idempotency check at `L31-35` falsely treats the offline sale as already synced (**skipping the order and skipping inventory deduction!**) or `IX_Orders_InvoiceNumber` throws a unique constraint violation and rolls back!
    - **Cash Drawer Shift Drift (`L150-174`):** Increments `openShift.ExpectedBalance += cashPaymentTotal` without incrementing `openShift.TotalCashSales += cashPaymentTotal`.

---

## Resolution Checklist (Preserving Inventory Deduction)

1. **`POS.Client` Compile & Checkout State (`NewSale.razor`, `LocalPosModels.cs`, `CartService.cs`, `DiscountModal.razor`):**
   - Remove `using Microsoft.Build.Tasks.Deployment.Bootstrapper;` from `LocalPosModels.cs:1`.
   - Track connectivity state in `NewSale.razor` (`bool isOffline => connectivityState != TerminalConnectivityState.OnlineAndHealthy;`, refreshed in `LoadCatalogAsync` and `ProceedToPayment`), use `Math.Max(0m, Cart.Subtotal - Cart.ActiveDiscountAmount)` instead of `Cart.TotalAmount`, and use `CreatedAt = DateTime.UtcNow` on `OrderResponseDto`.
   - Fall back to `LocalDiscountCalculationService` in `DiscountModal.razor` when offline.
   - Include `TaxName`, `Barcode`, `ShortCode`, `UnitOfMeasure`, and `ImageUrl` when projecting `LocalDb.Products` in `NewSale.razor:703-705`.
2. **Invoice Sequence Sync (`OfflineReferenceDataDtos.cs`, `OfflineReferenceDataController.cs`, `ReferenceDataSyncService.cs`):**
   - Fix `CostPrice = p.CostPrice ?? 0m` in `OfflineReferenceDataController.cs:51`.
   - Include the active `InvoiceSequence` for `outletId` in `OfflineReferenceDataResponseDto` and sync/upsert it into `_localDb.InvoiceSequences` in `ReferenceDataSyncService.SyncReferenceDataAsync` (preserving any higher local `LastIssuedNumber` if unsynced offline sales exist).
3. **SQLite Concurrency & Transaction Safety (`MauiProgram.cs`, `LocalPosDbContext.cs`, `OfflineCheckoutService.cs`, `OfflineTransactionSyncService.cs`):**
   - Change `LocalPosDbContext` registration in `MauiProgram.cs:39` from `AddSingleton` to `AddScoped` (or `AddDbContext<LocalPosDbContext>(ServiceLifetime.Transient)`), initializing WAL mode once at startup rather than on every UI/timer query.
   - Explicitly call `await transaction.RollbackAsync()` before early returns in `OfflineCheckoutService.ProcessOfflineCashSaleAsync` (`L70`, `L106`) and match calculated line items by index rather than `FirstOrDefault(ci => ci.ProductId == ...)`.
   - Fix `OfflineTransactionSyncService.UploadTransactionAsync` to update tracked `SyncOutbox` entities.
4. **Server Sync & Inventory Deduction Hardening (`OfflineSyncController.cs`):**
   - Keep the atomic `BranchInventories` deduction and `StockAdjustment` (`OFFLINE_SALE_SYNC`) creation in `OfflineSyncController.cs:107-129` intact.
   - Check idempotency via `OfflineSyncIdempotency` (`LocalTransactionId`) first so offline sales never falsely collide with online `InvoiceNumber`s or skip inventory deduction.
   - Advance server `InvoiceSequences.LastIssuedNumber` (if the synced offline invoice number is higher) and generate collision-free `OrderNumber`s (e.g., `OFF-{yyMMddHHmmss}-{LocalTransactionId[..6].ToUpper()}`).
   - Increment `openShift.TotalCashSales += cashPaymentTotal` alongside `openShift.ExpectedBalance += cashPaymentTotal` (`L161`).

---

## Resolution

1. **Isolated `LocalPosDbContext` & Serialized SQLite Write Lock (`LocalPosDbContext.cs`, `MauiProgram.cs`, `OfflineBackgroundSyncManager.cs`, `OfflineSyncCoordinator.cs`, `MainLayout.razor`, `Home.razor`):**
   - Added `LocalPosDbContext.WriteLock` (`SemaphoreSlim(1, 1)`), `Default Timeout=5`, `PRAGMA busy_timeout=5000;`, and a thread-safe `_isInitialized` guard on `InitializeDatabase()`.
   - Changed `LocalPosDbContext`, `IReferenceDataSyncService`, `IOfflineCheckoutService`, `IOfflineTransactionSyncService`, and `IOfflineSyncCoordinator` registrations in `MauiProgram.cs` from `Singleton`/`Scoped` to `Transient`.
   - Isolated every background polling timer (`MainLayout.razor`, `Home.razor`), sync coordinator lease check (`OfflineSyncCoordinator.cs`), outbox upload loop (`OfflineTransactionSyncService.cs`), reference sync (`ReferenceDataSyncService.cs`), and checkout operation (`OfflineCheckoutService.cs`) onto dedicated `using var db = new LocalPosDbContext()` instances, with writes serialized through `LocalPosDbContext.WriteLock`.
   - Wired `OfflineBackgroundSyncManager` to acquire/renew `IOfflineSyncCoordinator` lease before running background sync cycles.
2. **Pre-Allocated & Auto-Refilled `LocalInvoiceSequences` (`OfflineReferenceDataDtos.cs`, `OfflineReferenceDataController.cs`, `ReferenceDataSyncService.cs`, `OfflineCheckoutService.cs`):**
   - Fixed `CostPrice = p.CostPrice ?? 0m` in `OfflineReferenceDataController.cs`.
   - Added `OfflineInvoiceSequenceDto` to `OfflineReferenceDataResponseDto` and synced active `InvoiceSequences` into `LocalInvoiceSequences` in `ReferenceDataSyncService.cs` while preserving any higher local `LastIssuedNumber`.
   - Inside `OfflineCheckoutService.ProcessOfflineCashSaleAsync`'s atomic SQLite transaction, calculated all `decimal` tax/discount totals and verified `cashTendered >= totalAmount` (with explicit `await transaction.RollbackAsync()`) **before** allocating or incrementing the invoice sequence, and added automatic local range allocation/extension (`+100000`) when missing or exhausted so offline checkouts never abort or orphan sequence numbers.
3. **Preserved Atomic Transaction, `decimal` Precision & Server Inventory Deduction (`OfflineCheckoutService.cs`, `OfflineTransactionSyncService.cs`, `OfflineSyncController.cs`, `NewSale.razor`, `CartService.cs`, `DiscountModal.razor`):**
   - Fixed detached `AsNoTracking()` outbox status updates in `OfflineTransactionSyncService.cs` via tracked `UpdateOutboxStatusAsync` and deterministic `SHA256.HashData` payload hashes.
   - Preserved atomic `BeginTransactionAsync()` in `OfflineSyncController.SyncTransaction`, keeping `BranchInventories.StockQuantity` deduction and `StockAdjustments` (`OFFLINE_SALE_SYNC`) intact while adding `OfflineSyncIdempotencyRecords` tracking, collision-safe `OrderNumber` (`OFF-{yyMMddHHmmss}-{txSuffix}`), active `InvoiceSequence.LastIssuedNumber` advancement, and `openShift.TotalCashSales` accumulation.
   - Fixed `NewSale.razor` (`isOffline` tracking, `Cart.TotalAmount`, `OrderResponseDto.CreatedAt`, offline product fields, offline modifier loading), `CartService.cs` (`TotalAmount`), `DiscountModal.razor` (offline fallback to `LocalDiscountCalculationService`), and `LocalPosModels.cs` (removed invalid MSBuild namespace).
   - Verified `dotnet build Pos.Solution.slnx` succeeds with `0 errors` across all 5 projects.


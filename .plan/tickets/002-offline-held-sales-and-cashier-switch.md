---
id: "002"
title: "Enable Offline Held Sales and Safe Offline Cashier Switching"
parent: "../MAP.md"
labels:
  - wayfinder:task
status: closed
assignee: antigravity
---

## Question

Is it safe to allow Held Sales and Switch Cashier while the POS terminal is in Offline Mode, and how should both be implemented without breaking invoice numbering, inventory deductions, or terminal authentication security?

---

## Analysis & Decision

1. **Held Sales in Offline Mode — YES (Safe & Recommended):**
   - A held sale (`HeldSaleCreateDto` / `HeldSaleDto`) is strictly pre-payment, pre-invoice-sequence, and pre-inventory-deduction cart state scoped to a single `OutletId` (`HeldSalesController.cs:148`).
   - Persisting offline held sales in local SQLite (`LocalHeldSales`) via a hybrid `ClientHeldSaleService : IHeldSaleService` allows cashiers to park, view, resume, and cancel tickets while offline, and keeps locally parked tickets visible in `HeldSales.razor` even after the terminal reconnects to the server.

2. **Switch Cashier in Offline Mode — YES, ONLY with Hashed Local Credential & Outlet Fallback:**
   - Previously, clicking **Switch Cashier** (`MainLayout.razor:115-131`) while offline cleared `PosSessionService` and redirected to `/login`, which required live HTTP calls (`POST api/Auth/pin-login` and `GET api/Outlets/resolve-terminal`), locking the terminal out completely until internet returned.
   - Storing plaintext PINs on the client is unsafe, so `OfflineReferenceDataController` must only sync **SHA-256 PIN hashes** (`PinHash`) into `LocalCashiers` (and `Login.razor` also caches the SHA-256 hash on online login).
   - When offline, `Login.razor` verifies the entered 4-digit PIN against `LocalCashiers.PinHash` and resolves the terminal from `LocalOutletConfigs`, updating `PosSessionService` (`Session.Username`, `Session.Role`, `Session.Outlet`) so all subsequent offline orders, payments, and receipts record the new `CashierId`.

---

## Resolution

1. **Implemented Offline Held Sales (`ClientHeldSaleService.cs`, `LocalPosModels.cs`, `LocalPosDbContext.cs`, `MauiProgram.cs`):**
   - Added `LocalHeldSale` entity and `LocalHeldSales` SQLite table (`CREATE TABLE IF NOT EXISTS LocalHeldSales` in `LocalPosDbContext.InitializeDatabase()`).
   - Created `ClientHeldSaleService : IHeldSaleService` and registered it in `MauiProgram.cs`.
   - `CreateHeldSaleAsync`, `GetHeldSalesAsync`, `ResumeHeldSaleAsync`, and `CancelHeldSaleAsync` automatically persist and query `LocalHeldSales` under `LocalPosDbContext.WriteLock` when offline (using negative `HeldSaleId`s and `HOLD-OFF-*` references so they never collide with server IDs) and merge locally held tickets with server held tickets when online.
2. **Implemented Safe Offline Cashier Switching (`OfflineReferenceDataDtos.cs`, `OfflineReferenceDataController.cs`, `ReferenceDataSyncService.cs`, `MainLayout.razor`, `Login.razor`):**
   - Added `OfflineCashierDto` (`UserId`, `Username`, `Role`, `PinHash`) and `BusinessTypeId`/`BusinessType` to `OfflineReferenceDataResponseDto` and `LocalOutletConfig`.
   - `OfflineReferenceDataController` computes SHA-256 `PinHash` values (`SHA256.HashData`) so plaintext PINs are never transmitted or stored locally.
   - `MainLayout.razor` ensures the active `Session.Outlet` is cached in `LocalOutletConfigs` before signing out on `SwitchCashier` or `LockTerminal`.
   - `Login.razor` caches the SHA-256 `PinHash` on online login and falls back to `TryOfflinePinLoginAsync` (`LocalCashiers` + `LocalOutletConfigs`) when offline.


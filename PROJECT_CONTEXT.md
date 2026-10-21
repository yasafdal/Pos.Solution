# Pos.Solution — Historical Project Context

> Superseded for hierarchy and current workflow details by `project_content.md`. The following review describes the pre-refactor baseline; do not use its old machine-as-Outlet contracts for new development.

Reviewed: 2026-10-20. Scope: current working tree at `Pos.Solution`, including pre-existing uncommitted changes.

## 1. Purpose, evidence and assurance

Pos.Solution is a multi-branch Philippine retail and food-and-beverage Point of Sale and back-office platform. It combines a native cashier application, a central business API, a server-rendered administration website, and shared contracts/calculators. Offline operation is a substantial implemented subsystem, not merely a planned feature.

This document is the primary architecture/context reference for subsequent development. Recheck cited implementation before changing it: this is a dated snapshot, not a claim that later code remains identical. `project_content.md`, `.plan/MAP.md`, and `docs/BIR_RESEARCH.md` provide historical intent, but are not proof of current behavior or legal compliance.

### Evidence terminology

- **Implemented:** an identifiable production code path exists. This does not mean production-safe or end-to-end verified.
- **Partial:** implementation exists but an integration, validation, recovery mechanism or supported platform is incomplete.
- **Missing in inspected source:** no implementation was found within the reviewed application scope. This is not a claim about external infrastructure.
- **Executed:** a command was actually run during this review, with results below.
- **Static finding:** supported by code/operation ordering but not reproduced against a running POS deployment.

Source references use repository-relative `path:line` or `path:start-end`. No secret values are reproduced here.

### Executed verification

SDK: .NET `10.0.401` on Windows.

| Command | Result |
|---|---|
| `dotnet build POS.Api/POS.Api.csproj --no-restore --nologo -v:q` | Exit 0; 0 errors, 5 warnings; builds Shared dependency. |
| `dotnet build POS.Web/POS.Web.csproj --no-restore --nologo -v:q` | Exit 0; 0 errors, 6 warnings; builds Shared dependency. |
| `dotnet build POS.Client/POS.Client.csproj --framework net10.0-windows10.0.19041.0 --no-restore --nologo -v:q` | Exit 0; 0 errors, 16 warnings. |
| `dotnet run --project POS.Tests/POS.Tests.csproj --no-restore --verbosity quiet` | Exit 0; **22 passed, 0 failed**. Uses a disposable in-memory SQLite database, not the application databases. |

Builds report vulnerable dependencies: SQLitePCLRaw 2.1.11 and System.Security.Cryptography.Pkcs/Xml 9.0.x, among the warnings captured. API also reports nullable warnings and a source-embedded connection-string warning. These are existing restore/build advisory results, not a fresh comprehensive dependency audit.

No application GUI, API host, live PostgreSQL, production/local POS SQLite file, printer, installer or payment gateway was exercised. No Android/iOS/MacCatalyst build was performed. Builds generated normal bin/obj artifacts. No source, configuration, schema or migration changes were made. Existing dirty files were preserved. There was no project knowledge graph; graphify was unavailable. RTK was present and build output was compressed by the toolchain.

## 2. Repository and project architecture

The actual solution is `Pos.Solution.slnx`, not `POS.sln`. It includes four projects (`Pos.Solution.slnx:1-8`). A fifth project, `POS.Tests`, exists outside the solution.

```text
POS.Client (MAUI Blazor Hybrid cashier)
    |  HTTP JSON via Shared adapters and direct HttpClient
    v
POS.Api (ASP.NET Core business API) ---- EF Core/Npgsql ---- PostgreSQL
    ^
    |  HTTP JSON via Shared adapters and direct HttpClient
POS.Web (Blazor Interactive Server administration)
    |  separate scaffolded Identity subsystem
    +---- EF Core/SQL Server ---- Identity database

POS.Client ---- EF Core/SQLite ---- local reference cache + sales + outbox + cash ledger
POS.Shared ---- contracts, interfaces, HTTP adapters, local calculations, helpers
POS.Tests  ---- direct API controller/Shared checks with in-memory SQLite
```

### Responsibilities and dependencies

| Project | Responsibility | Important dependencies |
|---|---|---|
| `POS.Api` | Authoritative orders/payments, tax calculations, numbering, catalog, inventory, returns, cash ledger, receipts, reports, offline ingestion | ASP.NET Core .NET 10; EF Core 10.0.12; Npgsql EF provider 10.0.3; JWT bearer; BCrypt 4.2.0; Shared. |
| `POS.Client` | Native cashier UI, terminal/session, cart, online payment, offline cash checkout, SQLite cache/outbox, cash operations, Windows printing | MAUI Blazor Hybrid; EF SQLite/Design 10.0.0; QuickGrid 10.0.12; SweetAlert2 5.6.0; Shared. |
| `POS.Web` | Browser back-office CRUD, stock adjustment, reporting, sales/returns audit and cash-movement management | Blazor Interactive Server; EF/Identity SQL Server 10.0.8; QuickGrid; SweetAlert2; AdminLTE/Bootstrap; Shared. |
| `POS.Shared` | DTOs, 20 interfaces, thin HTTP adapters, offline financial calculator, shortcode/color helpers | .NET 10 BCL; no external PackageReference. |
| `POS.Tests` | Console regression harness for cash controls, selected transaction scope and approval rules | API + Shared; EF/Relational 10.0.12 and SQLite 10.0.0; no standard test discovery framework. |

Evidence: project `.csproj` files; `POS.Api/Program.cs:44-51`; `POS.Client/MauiProgram.cs:29-63`; `POS.Web/Program.cs:17-87`; `POS.Tests/POS.Tests.csproj:1-20`.

All three production executables reference Shared. Neither Web nor Client accesses the central business database directly. Web's Identity database is separate from API Users, not a second business-data store.

### Folder map

- `POS.Api/Controllers`: 30 route controllers for business operations and master data.
- `POS.Api/Models`: entities and three partial PosDbContext files.
- `POS.Api/Services`: discount, sequence, receipt and sales-report services.
- `POS.Client/Components/Pages`: cashier workflows and some placeholder pages.
- `POS.Client/Data`: local models, initialization and SQLite configuration.
- `POS.Client/Services`: cart/session, cache, checkout, connectivity, uploader/coordinator and printer abstractions.
- `POS.Client/Platforms`: native startup and platform implementations.
- `POS.Web/Components/Pages`: application administration pages.
- `POS.Web/Components/Account`, `Data`, `Auth`: parallel Identity scaffold and custom API-token authentication.
- `POS.Shared/Models`, `Interfaces`, `Services`, `Helpers`: contracts, adapters and pure helpers.
- `deployment`: Windows publish/Inno Setup scripts.
- `docs/BIR_RESEARCH.md`: intended regulatory baseline; not independently legally verified here.
- `.agents/skills/wayfinder/SKILL.md`: a real repository-local Wayfinder skill is present. `.plan/MAP.md` is its local planning map; linked ticket files are currently deleted in the working tree. No Wayfinder installation was performed by this review.
- `publish`, `bin`, `obj`, `.vs`, AdminLTE distributions and vendored assets are generated/vendor material, not additional implemented POS modules. Review emphasized authored behavior, not line-by-line vendor/binary auditing.

## 3. Database architecture and relationships

### 3.1 Central PostgreSQL business database

`PosDbContext` has **39 DbSets**, counted across `Models/PosDbContext.cs`, `.Extensions.cs`, and `.ServiceMode.cs`. Startup registers a scoped Npgsql context (`POS.Api/Program.cs:44-46`). Legacy Npgsql timestamp behavior is enabled (`:53-54`).

| Domain | Entities / tables represented |
|---|---|
| Organization and classification | Branch, Outlet, BusinessType, ServiceMode, User |
| Catalog and stock | Category, Product, Taxis/Taxes, UnitOfMeasure, ProductRecipe, BranchInventory, StockAdjustment |
| Customization | ModifierGroup, ModifierOption, ProductModifierGroup |
| Sales and numbering | Order, OrderItem, OrderItemModifier, Payment, PaymentMethod, OrderSequence, InvoiceSequence |
| Discounts and receipt audit | DiscountType, DiscountAuditLog, ReceiptReprintLog |
| Parked sales | HeldSale, HeldSaleItem, HeldSaleItemModifier, HeldSaleAuditLog |
| Reversals | TransactionVoid, SalesReturn, SalesReturnItem, Refund |
| Cashier and drawer | CashierSession, CashierSwitchLog, CashDrawerShift, CashDrawerMovement, CashMovementCategory |
| Synchronization | OfflineSyncIdempotencyRecord |

Major relationships:

- BusinessType/ServiceMode classify Branch; Branch owns Outlets and branch stock.
- Category and optional Tax classify Product. UnitOfMeasure is a standalone lookup; Product keeps a UOM string rather than a relational UOM foreign key.
- BranchInventory joins Branch/Product with a unique composite index (`PosDbContext.cs:63-82`). StockAdjustment records integer before/after movements.
- ProductRecipe links finished Product to ingredient Product with decimal required quantity and unique product/ingredient pair (`:146-162`).
- ProductModifierGroup links products/groups; groups own options (`PosDbContext.Extensions.cs:90-143`).
- Order owns items and payments; items own modifier snapshots (`:146-240`). Product IDs/names/prices on order lines are historical snapshots, not enforced product relationships.
- Order can reference InvoiceSequence and DiscountType. OrderNumber and InvoiceNumber have unique indexes (`:172-179`).
- Returns reference original order/items; refunds reference original payment and optional return/void. Delete behavior generally restricts original records, cascades return items, and sets nullable refund parent links null (`:382-476`).
- HeldSale owns held items/modifiers; separate audit records retain operations (`:306-380`).
- DrawerShift owns movements; movement category is optional (`:510-586`). ReferenceNumber and ClientMovementId have unique indexes.
- OfflineSyncIdempotency has unique local transaction and invoice identifiers (`:589-601`). PayloadHash exists, but replay comparison is not enforced in the sync controller.

Not all apparent business links are foreign keys: Order's BranchId, OutletId, CashierId and ShiftId are scalar fields without modeled navigation/FK enforcement in this mapping. Similar loose identity/location fields exist in held sales, shifts and cashier sessions. Application validation therefore carries substantial integrity responsibility.

### 3.2 Local SQLite terminal database

`LocalPosDbContext` exposes **19 DbSets** (`POS.Client/Data/LocalPosDbContext.cs:9-27`):

- Products, categories, modifier groups/options/product-group links, taxes, discounts.
- Outlet configuration and invoice sequences.
- Orders, order items and payments.
- Sync outbox and sync audit log.
- Held sales, cached cashiers.
- Cash-movement categories, drawer shifts and drawer movements.

Storage: a single `PosSolution/local_pos.db` beneath the OS user's LocalApplicationData (`:34-44`). There is no configured encryption or per-terminal file partitioning. Multiple terminal configurations share this file if run under the same OS user.

Initialization uses EnsureCreated plus hand-written ALTER/CREATE statements, not versioned migrations (`:85-199`). Foreign keys are disabled. WAL, NORMAL synchronization and busy timeout are configured (`:201-205`). Initialization is process-static; connection-scoped PRAGMAs should not be assumed applied to every future connection. ALTER failures are swallowed as presumed existing columns, potentially masking other failures.

Transient contexts and fresh contexts in checkout/upload/status paths replace the old singleton-context pattern. A process-wide WriteLock coordinates cooperating writers (`:29-32`); it is not a cross-process lock.

Local entities retain order/payment/tax snapshots. Modifier and customer detail survives in the outbox's original order JSON, but the reduced local order-item model does not retain all of it for later history/reprint reconstruction (`LocalPosModels.cs:141-219`; `OfflineCheckoutService.cs:236-246`; `OfflineTransactionSyncService.cs:83-134`). There is no local stock ledger or local return/void queue.

### 3.3 Web SQL Server Identity

`POS.Web/Data/ApplicationDbContext.cs:6` inherits IdentityDbContext. Identity schema migration/snapshot files are present under `POS.Web/Data/Migrations`. Startup registers stores, cookies, sign-in and token services (`POS.Web/Program.cs:29-48`). These are active registrations, not proof of successful database initialization or integration with API Users.

The main POS Web login uses API JWT and ProtectedSessionStorage instead. Account scaffold offers cookie/passkey workflows but does not establish the API bearer token required by business screens. Email delivery is a no-op. Do not describe this scaffold as completed POS MFA, unified user management or working password-reset delivery.

### 3.4 Schema and precision risks

- No PostgreSQL `.sql` files were found in the authored inventory. MigrationRunner references seven optional files, skips absent files, and then executes inline cash-movement schema SQL (`POS.Api/MigrationRunner.cs:15-36,38-139`). It is not a complete fresh-install baseline for all 39 entities.
- API startup runs that runner and logs errors but continues (`Program.cs:65-73`). API launch would change a reachable database; it was intentionally not used as a smoke test.
- Scaffolding, partial Fluent mappings, attributes, raw startup SQL, SQLite EnsureCreated/ad hoc patches and Web Identity migrations coexist. No single versioned business-schema migration authority is evidenced.
- Tax Rate has `.HasPrecision(18)` without explicit scale (`PosDbContext.cs:186-191`). Actual deployed PostgreSQL type was not queried; fractional-rate behavior requires provider/schema validation rather than assuming the deployed column truncates.
- Several order/item tax and discount fields lack explicit Fluent precision despite other money fields using `(18,2)` (`Models/Order.cs:43-54`; `OrderItem.cs:31-34`; Extensions mappings).
- Inventory/stock adjustment quantities are integers while API order quantities and BOM quantities are decimal. Client cart/offline quantities are also integers. Fractional ingredients/weighted sales cannot flow consistently without a deliberate quantity/UOM design.
- OrderSequences has no modeled unique OutletId constraint; concurrent initial sequence creation remains a risk. Global unique InvoiceNumber also requires outlet prefixes/series to be globally distinct.

## 4. Complete cashier workflows

### 4.1 Authentication and terminal assignment

1. Client accepts a four-digit PIN and posts `api/Auth/pin-login` (`Components/Pages/Login.razor:39-85,323-379`; Shared `UserService.cs:19-29`).
2. API performs a PIN lookup and issues an eight-hour JWT. Password login separately uses BCrypt (`AuthController.cs:25-115`).
3. Client resolves a terminal via an authenticated outlet request using a detected address; API returns outlet/branch data and terminal-scoped token claims (`Login.razor:545-565`; API `OutletsController.cs:28-102`). IP lookup is not device authentication.
4. Client stores in-memory session and shared bearer header; cashier session activation and reference sync are best-effort (`Login.razor:567-585`).
5. Offline login compares cached SHA-256 PIN hashes and selects cached outlet configuration (`:382-517`). No local throttling, expiry or revocation mechanism is implemented.

Important gap: the offline fallback also occurs after a non-success online login, not only a network outage. A rejected credential still cached locally can therefore authenticate offline (`Login.razor:590-613`). Session expiry/refresh and reconnect reauthentication are not implemented in PosSessionService (`:7-30`). Lock/switch clears session/cart and header but does not submit server session logout (`MainLayout.razor:192-217`; CartService `:10-16,164-168`).

### 4.2 Catalog, cart and discounts

NewSale supports exact business classifications RETAIL and FNB, not arbitrary labels (`NewSale.razor:642-670`). Retail provides barcode/SKU/name search, whole-number quantity and keyboard handling. FNB provides category/product cards and required/min/max modifier selection (`:769-1061`). Modifier-load errors can fall back to adding a bare product, bypassing required choices.

Cart merges identical product/modifier combinations, preserves different modifier sets, and calculates modifier-inclusive gross totals (`CartService.cs:50-161`). Stored discount amount is not recomputed automatically after quantity changes.

Discount modal tries API then local calculation. It uses hardcoded VAT-registered/VATable assumptions and a built-in fallback discount list rather than the cached discount catalog (`DiscountModal.razor:89-161`). Final checkout may recalculate using different context. Manual percentage preview is not fully forwarded through the normal order-create contract.

### 4.3 Online sale, settlement and invoice

1. ProceedToPayment creates an unpaid server order before collecting tender (`NewSale.razor:1223-1268`).
2. OrdersController opens a transaction, issues operational order number, calculates totals, stores items/modifiers and discount audit, then commits (`OrdersController.cs:16-142`).
3. Client validates tender/splits/references and posts payment (`NewSale.razor:1353-1497`).
4. PaymentsController validates order/payment amounts, persists successful payment rows, marks PAID/COMPLETED, issues legal invoice and updates cash drawer totals for CASH (`PaymentsController.cs:16-162`).
5. Successful payment clears cart and opens receipt. Printing is separate and does not repeat payment.

Order sequence and active invoice series use PostgreSQL `FOR UPDATE` inside caller transactions. Invoice issuance returns the existing number if already assigned and enforces configured end number (`SequenceService.cs:9-115`).

Partial behavior: pricing/identity/tax/eligibility comes from request rather than authoritative catalog/claims. Payment has no robust idempotency key or external processor confirmation. A timed-out order creation can leave an ambiguous committed unpaid server order before local fallback. A timed-out payment does not silently generate a second offline sale, but explicit reconciliation/recovery is absent.

### 4.4 Offline sale and upload

1. Offline reads local references. Readiness checks integrity and any active product, not complete outlet/cashier/sequence/shift readiness (`OfflineCheckoutService.cs:37-44`).
2. Checkout takes WriteLock, uses a fresh context and SQLite transaction, recalculates totals, validates cash, issues local number, persists order/items/payment and optional drawer cash effect, writes outbox and commits (`:59-250`). Offline settlement is cash-only; split UI is not offline multi-tender support.
3. Original request JSON preserves modifiers/customer metadata for upload. Normal offline calculation mapping uses item index rather than ProductId.
4. Active sync manager is registered with health checks, per-cycle semaphore and coordinator leases (`MauiProgram.cs:45-46`; `OfflineBackgroundSyncManager.cs:28-89`). Registration was verified; hosted-service lifecycle execution in MAUI was not dynamically proven.
5. Uploader reconstructs snapshots and JSON modifiers, sends local transaction ID/hash/version metadata, then records ACK/retry/attention state (`OfflineTransactionSyncService.cs:54-239`).
6. API ingestion validates some outlet relationships, checks transaction/invoice idempotency, persists sale/payment, deducts stock, updates drawer, stores idempotency record and commits (`OfflineSyncController.cs:22-242`). It does not recalculate/validate the financial snapshot or require authentication.

Retry policy uses bounded delays/jitter and attempt cap. Cash-movement conflicts become local CONFLICT and outbox NEEDS_ATTENTION. Stale lease recovery runs once at startup; UPLOADING items not expired then can remain excluded from future selection. There is no attention-resolution/requeue UI (`OfflineSyncCoordinator.cs:25-93`; `OfflineTransactionSyncService.cs:28-37,176-196,319-330`). Two legacy upload workers remain unregistered; their presence does not mean concurrent active workers.

Offline invoice numbers are **not exclusively server-reserved**. Client can synthesize/extend ranges and merge local/server counters (`OfflineCheckoutService.cs:106-135`; `ReferenceDataSyncService.cs:274-322`). Concurrent online/offline issuers can collide; uniqueness/idempotency cannot substitute for fiscal range ownership.

### 4.5 Reference-cache refresh defect

Static finding with direct operation-order evidence: ReferenceDataSyncService deletes reference tables and tracks replacement entities. When cash categories are nonempty, it calls `db.ChangeTracker.Clear()` after those Add calls but before the only SaveChanges. Pending outlet/cashier/product/category/tax/discount/modifier inserts detach, while raw DELETEs remain (`ReferenceDataSyncService.cs:64-216,324-345`). Categories/shift/sequence added afterward may save, leaving an incomplete cache. This was not reproduced against the user's SQLite file.

Reference refresh is global replacement, not a multi-outlet merge. Missing active server shift does not reliably invalidate old local open shifts. Cached server balances can overwrite pending local effects. No active periodic full-reference refresh was found; login is the explicit refresh trigger.

## 5. Business modules and current status

| Module | Implemented behavior | Incomplete/missing behavior |
|---|---|---|
| Branches/outlets | Classification, outlet/device address, tax flags, invoice-series management | Weak server scope/authorization; no robust authenticated device identity. |
| Product/catalog | Categories, products, images, SKU/barcode/shortcode, UOM/tax, modifier definitions/links | Client-controlled sale prices; inconsistent failure handling; inactive/raw-material filtering differs offline. |
| Recipes/inventory | Recipe CRUD, per-branch stock, manual adjustment/audit, offline upload stock decrement, return restock | **No online-sale stock deduction or recipe/BOM consumption**; integer stock; weak concurrent stock policy; return restock lacks equivalent stock audit. |
| Discounts/taxes | VAT/non-VAT/exempt/zero-rated; SC/PWD/NAAC/MOV, SP, BNPC, employee/manual branches; audit | Hardcoded rates/rules, duplicated calculators, caller-controlled eligibility, incomplete configured/PROMO/manual wiring; no established compliance sign-off. |
| Online orders/payments | Unpaid order, split/single tender, change, paid state, numbering, cash drawer integration | Public write endpoints; weak payment retry/concurrency controls; no live GCash/Maya/card processor integration. |
| Held sales | API parked sale CRUD/resume concurrency checks; local JSON-backed hold/resume/cancel | Local holds are not synchronized or cross-terminal transferable; positive server holds are not generally available offline. |
| Cashier sessions | API activation/handoff logs and client switch/lock | Client logout/switch not fully journaled server-side; offline session lifecycle incomplete. |
| Cash drawer/petty cash | Open/close, paid-in/out, petty cash/drop, approvals/reject/reverse, categories, vouchers, reconciliation | Offline-opened shifts have no upload lifecycle; final close requires online; local/server pending balances can drift. |
| Returns/voids | Authenticated API, supervisor PIN, serializable transactions, prior-return checks, refunds and optional restock, online client dialogs | Over-refund and duplicate request-line risks; scope inconsistencies; offline returns absent; standalone Returns page placeholder. |
| Transactions | Claim-scoped cashier history/detail, Philippine 04:00 business-day behavior, local history fallback | Local history only offline-created sales, not online cache; first-100 record limits; reconnect does not merge pending local rows. |
| Receipts/reprints | Paid gating, snapshots, ownership checks, best-effort audit, Windows HTML print/PDF | Supervisor header substitution; offline reprint lacks equivalent approval/audit; physical print unverified; non-Windows unsupported. |
| Sales reporting | Role-gated date/location/cashier/payment/grouped totals | DateFrom adapter omission; return/refund deduction missing; split-method attribution wrong; different business-day/date semantics. |
| Web administration | Master-data CRUD, stock adjustment, sales/report/audit screens, cash-movement/category management | Weak route-role consistency; parallel auth stacks; fake export acknowledgement; Web reprint audit without actual print. |
| Deployment | Windows Client publish and Inno Setup packaging scripts/output | Endpoint parameter unused; backend deployment absent; unchecked native exits; signing/prerequisite/upgrade validation absent. |

### Returns and financial consistency details

OrdersController stores gross `unitPrice * quantity` as item LineTotal but discounted calculation as order TotalAmount (`OrdersController.cs:94-120`). ReturnsController refunds `LineTotal / Quantity` and records allocated discount separately without subtracting it (`ReturnsController.cs:348-365`). Discounted online sales can consequently be refunded above net paid. Additionally, previously returned quantities are computed once and not increased for repeated same-item lines within one request (`:326-392`). Both are static, high-priority financial correctness findings.

OrdersController matches calculated items by first matching ProductId (`:98`), so duplicate product lines with different modifiers can receive incorrect line tax/discount snapshots. Offline's index-based mapping does not fix this online path.

### Cash reconciliation

The implemented drawer model separates operating cash movements from sales/VAT/invoice counters. Opening float plus cash sales/refunds and posted cash in/out/drop determines expected cash, with counted closing cash and discrepancy. Approval-required movements can remain PENDING_APPROVAL; close checks pending approvals. Online payment/refund integration exists now and contradicts the old architecture review.

Offline local shifts use positive Unix-seconds identifiers, not negative IDs (`CashDrawer.razor:994-1019`). Only order and cash-movement upload types are handled; no shift-create/close synchronization is implemented. Do not assume server mapping of a local-only shift exists.

### Printing and fiscal output

Current Windows printing is isolated WebView2 HTML, not GDI+/System.Drawing or a hardcoded thermal printer (`Platforms/Windows/WindowsReceiptPrinter.cs:18-147`). It tries the OS default queue then opens a dialog fallback. Dialog closure is not proof paper printed, and cancellation may still appear successful. PDF export uses WebView2 and a sanitized filename in Documents/POS_Receipts. Other platforms use UnsupportedReceiptPrinter.

HTML encoding/CSP/navigation restrictions are present (`ReceiptHtmlFormatter.cs:14-24,134-146`; `ReceiptNavigationPolicy.cs:5-10`). The formatter explicitly says **NOT A BIR TAX INVOICE** (`ReceiptHtmlFormatter.cs:130`). A pure zero-rated sale can omit the tax section due to its outer condition (`:102-107`). Consequently invoice numbering and tax fields must not be presented as evidence of BIR-accredited fiscal output.

## 6. Web and Shared integration details

Web's main custom login calls API password login and stores JWT in ProtectedSessionStorage. Restore decodes token/dates, builds UI claims and sets outbound bearer header; it does not cryptographically validate the token locally. API validates bearer tokens. UI visibility is not API authorization (`POS.Web/Auth/CustomAuthStateProvider.cs:20-69`).

Business pages use stateful Razor code, QuickGrid, modal forms and SweetAlert confirmations. MainLayout hides body for anonymous users; only selected pages have explicit Authorize/role attributes. Most menu links are visible to any signed-in user. `/users` is Admin/Manager restricted in Web, but the API endpoint itself is public.

Coverage navigation: product/category/modifier CRUD, branches/outlets/series, stock adjustment, business/service/UOM lookups, discounts, user list/create/delete, read-only held sales, sales history/reports, return/void audit, and cash-movement administration. `/manage-discounts` lacks corresponding navigation. Web `/returns-management` export only displays success text; `/sales-history` reprint requests audit but does not print. The history pager operates locally on the first 100 records (`SalesHistory.razor:412-486`; `ReturnsManagement.razor:224-227`).

Shared combines interfaces/contracts with concrete HTTP SDK wrappers, not a pure domain layer. Adapters vary: Product/Category ignore update/delete HTTP status; Order/Payment/HeldSale can return null; Discount/Receipt enforce failure more explicitly. This produces inconsistent UI success/fallback semantics (`POS.Shared/Services/ProductService.cs:23-40`; `CategoryService.cs:23-41`; `OrderService.cs:10-14`).

SalesReportApiService omits DateFrom although model and Web bind it (`POS.Shared/Services/TransactionServices.cs:87-97`). Payment-method reports select orders using Any then sum all successful tenders; refunds are not deducted (`POS.Api/Services/SalesReportService.cs:16-52`).

Calculators are duplicated in API and Shared. They round per line, hardcode VAT/rates and largely ignore configured TaxRate/PROMO inputs. Ordinary percentage discounts do not recalculate baseline VAT. Manual discount bounds/negative inputs are not comprehensively guarded. These are implementation observations, not legal advice; establish accountant-approved expected cases before changing formulas.

## 7. Security and reliability risk register

Priority below reflects potential impact, not live exploitation testing.

| Priority | Confirmed source finding | Evidence |
|---|---|---|
| Critical | Public Users list/create/delete; plaintext PIN response; client-selected role creation | `POS.Api/Controllers/UsersController.cs:8-81`; no global fallback in `Program.cs`. |
| Critical | Orders/payments/inventory and many master-data controllers lack authorization | Controller declarations; `POS.Api/Program.cs:75-89`. |
| Critical | Anonymous offline ingestion trusts financial snapshots and identities; hashes are stored but not compared on replay | `OfflineSyncController.cs:12-23,90-188,219-242`. |
| Critical | Anonymous reference data exposes PIN-derived hashes; offline movement approval accepts same hash as proof | `OfflineReferenceDataController.cs:152-165`; `OfflineSyncController.cs:377-402`. |
| High | Reference refresh detaches unsaved replacement entities after deletes | `ReferenceDataSyncService.cs:64-216,330`. |
| High | Gross discounted-sale refunds and repeated return lines can over-refund/restock | `OrdersController.cs:94-120`; `ReturnsController.cs:326-392`. |
| High | No online stock/BOM consumption, unlike offline sync | Orders/Payments paths; `OfflineSyncController.cs:149-170`. |
| High | Credentials/signing configuration embedded in tracked source/settings | `PosDbContext.cs:38-44`; `MigrationRunner.cs:9-13`; API appsettings configuration structure. Values intentionally omitted. |
| High | Web disables outbound certificate verification unconditionally | `POS.Web/Program.cs:50-64`. |
| High | Reprint replaces shared bearer header with supervisor token without restoration | `POS.Client/Components/Pages/Transactions.razor:592-625`. |
| High | Rejected online PIN can fall back to cached offline credential; short unsalted PIN hashes | `Login.razor:382-517,590-613`. |
| High | Hardcoded unencrypted localhost API conflicts with Release terminal-address resolution and remote/mobile use | `MauiProgram.cs:30-33,64-75`; `TerminalAddressProvider.cs:31-54`. |
| High | Offline invoice ranges not exclusively reserved; local shifts lack server lifecycle | `OfflineCheckoutService.cs:106-135`; `CashDrawer.razor:994-1019`. |
| High | Dependency vulnerability advisories emitted by successful builds | Executed checks in section 1. |
| Medium | One-time stale lease recovery can leave UPLOADING excluded indefinitely | `OfflineSyncCoordinator.cs:84-93`; manager startup; uploader pending selector. |
| Medium | Incomplete schema baseline, weak upgrade visibility, precision/UOM mismatch | Section 3; MigrationRunner and local initialization. |
| Medium | Reports/history/export may mislead; reprint audit is best-effort | Reporting/adapters/pages; ReceiptsController `:35-48`. |
| Medium | Role spellings vary (`SeniorCashier` vs `Senior Cashier`); dates mix UTC/local/04:00 business day | Role helpers; TransactionsController `:27-34`; report date filtering. |

CORS currently permits any origin/method/header. OpenAPI/Scalar mapping is commented out despite packages being present (`POS.Api/Program.cs:56-61,82-87`). No global rate limiting or PIN lockout was evidenced. Do not expose the API publicly in its current authorization state.

## 8. Tests, packaging and remaining assurance

POS.Tests is a console executable with 22 explicit scenarios, not xUnit/NUnit/MSTest; its banner still says 18 (`POS.Tests/Program.cs:49-75`). It directly instantiates controllers with claims and uses in-memory relational SQLite after removing PostgreSQL cast defaults. It does not host HTTP and cannot validate middleware, authorization attributes, JWT handling, routing or serialization.

The executed 22/22 result is useful regression evidence for cash movements, approvals, balances, reversal, selected sync rules and transaction scope. It is not full system certification. Named coverage exceeds some implementation: concurrency is sequential; retry/history checks partly reimplement simplified logic; printing checks prefixes rather than real printer output. Tests reference API/Shared, not MAUI or Web, and are outside the solution. PostgreSQL FOR UPDATE behavior is not established by SQLite tests.

Deployment scripts publish only Windows Client and optionally invoke Inno Setup. ApiUrl parameter is unused, native exit codes are unchecked and stale publish output can be packaged (`deployment/build-installer.ps1:1-25`). Installer metadata/prerequisite/signing/upgrade validation is incomplete (`installer.iss:3-41`). Existing EXE output is not proof that installation or launch currently works. API/Web/database hosting is not covered by these scripts.

### Unverified or missing assurance

- Fresh PostgreSQL deployment/upgrade and actual schema alignment.
- Hosted negative authorization tests, brute-force controls and role/location matrix.
- Real end-to-end online sale/payment/refund and ambiguous timeout recovery.
- Reference refresh with populated cash categories, restart durability and multi-process contention.
- Simultaneous online/offline invoice issue and conflict recovery.
- Offline shift open/reconnect/reconciliation, denied cached PIN and expiring token behavior.
- Production calculator parity and accountant-approved tax/discount/rounding cases.
- Printer output/dialog cancellation, pure zero-rated output, receipt audit guarantees.
- Release terminal discovery, clean-machine installation and mobile support.
- Export/download and complete historical pagination.
- Fiscal accreditation, Z-reading counters, CWT/withholding workflows and production BIR sign-off. Repository research describes intent; this review did not verify current legislation.

## 9. Corrections to prior project documentation

The previous `project_content.md` should not be copied as current truth:

- Windows client currently builds; old undeclared isOffline/Cart.TotalAmount/OrderDate compilation claims do not describe this checkout.
- Local context is transient/fresh-context based, not a shared singleton.
- PostgreSQL DbSets are 39; local DbSets are 19, not the older counts.
- Online cash payments and cash refunds now update drawers.
- Returns controller is authenticated and PIN-only approval has replaced the old username-as-PIN bypass. Other serious authorization/PIN disclosure weaknesses remain.
- Offline sequences, lease acquisition, tracked upload status, local holds and cached cashier login exist.
- Offline modifiers survive in outbox/upload but not every history/reprint representation.
- Missing optional SQL files are skipped; MigrationRunner also contains inline schema changes. It is still not a complete migration strategy.
- Printing is WebView2/default-printer/dialog/PDF, not the old GDI+ implementation.
- Offline local shift IDs are positive Unix seconds; conflict status is local CONFLICT/outbox NEEDS_ATTENTION.
- Web Login has duplicate injection names in source (`Login.razor:6,9`), but the executed Web build succeeds. It is not recorded as a current compile blocker.
- `.plan/MAP.md` includes outdated frontier/status statements and links to deleted tickets. Historical completion claims are not independent verification.

## 10. Conventions and future-development reference

The application uses controller-centric EF business logic, a few focused backend services, partial scaffolded database mappings, thin Shared HTTP wrappers, and substantial component-local Razor state/logic. Financial snapshots and string status codes are pervasive. Preserve snapshot/audit semantics when changing money flows. Do not treat every status string or role spelling as interchangeable.

For a subsequent task, start here, then re-read the relevant cited files and trace callers. Prefer existing Shared contracts and native patterns. Verify server authorization independently from UI restrictions. Treat PostgreSQL business schema, SQLite cache schema and offline contract changes as one integration concern. Money/discount/stock changes require explicit online/offline/refund tests rather than build-only validation.

No remediation was implemented during this review. The platform has broad implemented functionality and successful Windows builds, but the identified trust boundaries, financial inconsistencies and offline recovery defects prevent a defensible production-readiness claim.

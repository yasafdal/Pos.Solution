# POS.Client — Comprehensive QA Test Script & Checklist

> **Scope:** Full end-to-end QA verification of `POS.Client` (.NET MAUI Blazor Hybrid — Windows x64 & Android Tablet).  
> **Target Release:** `v1.0.4` (`versionCode=5`) | **Timezone / Business Day Cutoff:** `04:00 AM Asia/Manila (PHT)`  
> **Note:** Every test case in this script maps directly to implemented screens, services, hardware drivers, and security boundaries in `POS.Client`.

---

## QA Execution Metadata & Sign-Off

| Field | Value |
|---|---|
| **App Version / Build** | `v1.0.4` (Windows `POS_Client_Setup.exe` / Android `com.agres.pos.client-v1.0.4-universal-signed.apk`) |
| **Environment / API URL** | UAT (`https://dev-uat.agressolutions.com/`) / Staging / Production |
| **Platform & OS** | `[ ] Windows 10/11 x64` &nbsp; `[ ] Android Tablet (API __)` |
| **Printer / Drawer Hardware** | `[ ] Ethernet/LAN (IP:________:9100)` &nbsp; `[ ] USB / OTG (Model:________)` &nbsp; `[ ] 80mm` / `[ ] 58mm` |
| **Test Terminal(s)** | Terminal 1: `________` &nbsp;|&nbsp; Terminal 2: `________` |
| **QA Tester** | `________________________` |
| **Execution Date** | `YYYY-MM-DD` |
| **Overall Result** | `[ ] PASS` &nbsp; `[ ] FAIL` &nbsp; `[ ] BLOCKED` |

---

## Pre-Test Preparation & Required Test Data

Ensure the following are configured in `POS.Web` / PostgreSQL before starting:

1. **User Accounts (Client Organization #1):**
   - `Client Admin` (e.g., `NENTINA`, Role: `Admin`, with PIN)
   - `Manager` (e.g., `MREFORMO`, Role: `Manager`, with PIN)
   - `Senior Cashier` (Role: `Senior Cashier`, with PIN)
   - `Cashier A` (Role: `Cashier`, with PIN)
   - `Cashier B` (Role: `Cashier`, with PIN)
   - `AGRES System Admin` (e.g., `admin`, in `AgresSystemAdmins` — **negative test only; must NEVER sync or log in to POS.Client**)
2. **Hierarchy & Terminals:**
   - At least **1 Branch** (configured with Receipt Logo, Tagline, VAT Display toggle) and **1 Outlet** (with `DefaultOpeningFund` configured).
   - **2 Active Terminals** (`Terminal 1` and `Terminal 2`) with active BIR `InvoiceSequences`, active `TerminalLicenses`, and provisioned `ZReadingTerminalStates`.
3. **Catalog & Discounts:**
   - Regular fixed-price product (Kitchen routed) + Drink product (Bar routed).
   - Weight-based product (`PricingMethod = Weight`, e.g., Lechon) with both linked inventory modifier and unlinked complimentary modifier.
   - Modifier groups: required single-select, optional multi-select (min/max limits).
   - Discounts: Statutory `SC` (Senior Citizen, Requires ID), `PWD` (Requires ID), Promotional `%`, Corporate Fixed `₱`, and `MANUAL`.
4. **Payment Methods:** `CASH`, `CARD`, `GCASH` (Requires Reference), `BANK` (Requires Reference).
5. **Cash Movement Categories:** `CASH_IN` (no approval), `PETTY_CASH` (approval required), `CASH_OUT` (threshold), `CASH_DROP` (approval required).

---

## Module 1 — Device Activation, Server Config & Backup Restore (`/activate`)

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-01.1** | P0 | Fresh Launch Redirect | Launch `POS.Client` on an unprovisioned device (no `local_pos.db`). | App automatically redirects to `/activate` (**One-Time Device Activation**). | `[ ] Pass` `[ ] Fail` |
| **QA-01.2** | P1 | Activation Code Validation | Enter fewer than 6 digits, then enter an invalid 6-digit code `000000` and click **Activate Device**. | Button stays disabled until 6 digits are entered; invalid code shows red error badge (`Invalid activation code...`). | `[ ] Pass` `[ ] Fail` |
| **QA-01.3** | P0 | Valid 6-Digit Activation & UUID Binding | Generate a 6-digit Activation Code as AGRES System Admin in `POS.Web`, enter it on `/activate`, and click **Activate Device**. | Device generates persistent UUIDv4, binds to terminal, downloads BIR details (PTU, MIN, Serial #), caches license & staff PIN hashes, and navigates to `/login`. | `[ ] Pass` `[ ] Fail` |
| **QA-01.4** | P0 | Consumed Activation Code Rejection | Attempt to activate a second device with the same already-used 6-digit code. | Rejected (`This activation code has already been used.`). | `[ ] Pass` `[ ] Fail` |
| **QA-01.5** | P1 | Encrypted Backup Restore (`.agresbak`) | Uninstall/clear app data on a device with an existing `.agresbak` backup; click **Restore from Tablet Backup** on `/activate`. | Restores `DeviceUuid`, terminal config, license package, and unsynced local data; tampered/corrupted `.agresbak` file is rejected with error. | `[ ] Pass` `[ ] Fail` |
| **QA-01.6** | P2 | Dev/UAT Server Switcher (if enabled) | Tap the server endpoint link in the footer on `/activate` or `/login`. | Switches API URL cleanly and persists selection across restart. | `[ ] Pass` `[ ] Fail` |

---

## Module 2 — Terminal Provisioning & Initialization Checklist (`/initialization`)

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-02.1** | P0 | First Login Auto-Routes to Setup | Log in for the first time after activation on an uninitialized terminal. | Navigates to `/initialization` (**POS Terminal Provisioning**). | `[ ] Pass` `[ ] Fail` |
| **QA-02.2** | P0 | Full Provisioning Run | Click **Start Initialization** while online. | Progress bar reaches `100%`; all steps pass: API health, terminal/branch/outlet config, staff PIN hash caching, master catalog & BIR invoice sequence allocation. | `[ ] Pass` `[ ] Fail` |
| **QA-02.3** | P0 | BIR Invoice Block Preservation | Re-open `/initialization` from sidebar (**Terminal Setup**) and re-run initialization twice. | Does **not** skip or burn BIR invoice sequence numbers when the local allocated block still has remaining capacity. | `[ ] Pass` `[ ] Fail` |
| **QA-02.4** | P1 | Network Failure & Retry | Disconnect network mid-setup or before clicking **Start Initialization**; reconnect and click **Retry Failed Steps**. | Failed step shows red X icon and clear error message; retry completes cleanly without corrupting SQLite. | `[ ] Pass` `[ ] Fail` |
| **QA-02.5** | P0 | Staff Cache Role Isolation | Inspect cached staff count after initialization; try logging in offline with AGRES System Admin credentials. | Only active client users from the terminal's organization are cached; AGRES System Admin is never cached. | `[ ] Pass` `[ ] Fail` |

---

## Module 3 — Authentication, Logout, Switch Cashier & Shift Ownership (`/login`, `MainLayout`)

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-03.1** | P0 | Online & Local-First PIN Login | Enter valid Cashier A PIN on `/login` while online. | Authenticates cleanly, loads terminal config (including `DeviceIdentifier`/`Uuid`), activates cashier session, and navigates to Dashboard (`/`). | `[ ] Pass` `[ ] Fail` |
| **QA-03.2** | P0 | Invalid / Inactive / AGRES PIN Rejected | Enter wrong PIN, deactivated user PIN, and (if tested) AGRES admin credentials. | Login denied with error alert; no session token granted. | `[ ] Pass` `[ ] Fail` |
| **QA-03.3** | P0 | Logout Custom Modal (Empty Cart) | Click **Logout** in the top header with an empty cart. | Displays custom dark-themed confirmation modal (no browser-native `window.confirm`). Clicking **Cancel** stays logged in; clicking **Logout** signs out to `/login` WITHOUT closing the open financial shift. | `[ ] Pass` `[ ] Fail` |
| **QA-03.4** | P0 | Switch Cashier Custom Modal (Empty Cart) | Click **Switch Cashier** in top header or on `/new-sale` with an empty cart. | Displays custom dark-themed **Switch Cashier?** modal with user-switch icon and **Cancel** / **Switch Cashier** buttons (no browser-native dialog). Confirming signs out to `/login`. | `[ ] Pass` `[ ] Fail` |
| **QA-03.5** | P0 | Unheld Cart Guard on Logout & Switch Cashier | Add 2 items to cart on `/new-sale` (do not hold or pay). Click **Logout** or **Switch Cashier**. | Modal warns about unheld cart items and offers **Hold Sale & Continue** OR explicit discard confirmation checkbox before allowing sign-out. | `[ ] Pass` `[ ] Fail` |
| **QA-03.6** | P0 | Single Open Shift Ownership (Online) | Cashier A opens a shift on Terminal 1, then clicks **Logout** (without Close Cashier). Attempt to log in as Cashier B on Terminal 1. | Login screen displays open-shift ownership banner for Cashier A; Cashier B is **blocked (409)** from logging in, opening a shift, or making sales on Terminal 1. | `[ ] Pass` `[ ] Fail` |
| **QA-03.7** | P0 | Same Cashier Resumes Open Shift Without Duplicates | Following QA-03.6, log back in as Cashier A on Terminal 1. | Cashier A logs in cleanly, resumes the **exact same open shift** (no duplicate `CashDrawerShifts` or duplicate active `CashierSessions`), and can sell normally. | `[ ] Pass` `[ ] Fail` |
| **QA-03.8** | P0 | Shift Ownership Released on Close Cashier | Cashier A completes **Close Cashier** on Terminal 1. Log in as Cashier B on Terminal 1. | Cashier B logs in cleanly and can open a new shift on Terminal 1. | `[ ] Pass` `[ ] Fail` |
| **QA-03.9** | P0 | Account Switch Clears Prior Role & Token | Sign in as `Admin` -> Switch Cashier to `Cashier A` -> check nav & `/transactions` -> Switch to `Manager`. | No prior `Admin` permissions or bearer token leak into `Cashier A`; UI and API enforce each active user's exact role immediately. | `[ ] Pass` `[ ] Fail` |

---

## Module 4 — Dashboard, Header Bar & Navigation Shell (`/`, `MainLayout`, `NavMenu`)

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-04.1** | P1 | Role-Based Sidebar & Bottom Bar Visibility | Log in as `Cashier`, then `Senior Cashier`, then `Manager`/`Admin`. Inspect sidebar (`NavMenu`) and bottom workspace bar. | `Cashier`: sees New Sale, Dashboard, Held Sales, Cashier/My Shift, Terminal Setup, Printer & Cash Drawer, License Info — **hides** Transactions & End-of-Day Z-Reading. `Senior Cashier` / `Manager` / `Admin`: sees Transactions & Z-Reading. | `[ ] Pass` `[ ] Fail` |
| **QA-04.2** | P1 | Connectivity & Sync Status Badges | Toggle network Off/On and observe top header badge and Dashboard sync panel. | Accurately transitions between `Online / Synced`, `Offline`, `X pending`, and `X failed` (with readable tooltip/error message); Last Sync timestamp displays in Philippine Time (`Asia/Manila`). | `[ ] Pass` `[ ] Fail` |
| **QA-04.3** | P1 | Manual Sync Button | Queue an offline sale, reconnect internet, and click **Sync Now** on Dashboard. | Sync spinner activates, outbox drains to `0 pending`, synced count increments, and order appears in `POS.Web`. | `[ ] Pass` `[ ] Fail` |
| **QA-04.4** | P1 | Held Sales Counter Badge | Hold a sale on `/new-sale` and check header / Dashboard held count. | Held sales badge increments immediately and decrements when resumed or cancelled. | `[ ] Pass` `[ ] Fail` |
| **QA-04.5** | P2 | License Expiry Banner in Header | Test on a terminal whose license is within 90/60/30/7 days of expiry. | Displays non-blocking expiration warning banner linking to `/license-info`. | `[ ] Pass` `[ ] Fail` |

---

## Module 5 — New Sale Register, Menu Display, Weight Pricing & Modifiers (`/new-sale`)

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-05.1** | P0 | Active Shift Guard | Attempt to complete checkout without an open shift. | Prompts/blocks sale until cashier opens a shift on this terminal. | `[ ] Pass` `[ ] Fail` |
| **QA-05.2** | P1 | Menu Display Modes (`TextOnly` vs `ImageAndText`) | Configure Terminal in `POS.Web` as `TextOnly`, sync & open `/new-sale`; switch to `ImageAndText`, sync & reload. | `TextOnly` renders crisp text cards with zero broken images; `ImageAndText` loads product images from configured UAT/API `ApiBaseUrl` (never `localhost`). | `[ ] Pass` `[ ] Fail` |
| **QA-05.3** | P1 | Category Custom Sorting, Colors & Search | Verify category tabs order/colors against `POS.Web`; test search by product name, short code, and barcode. | Categories render in configured `DisplayOrder` with custom `ColorHex`; search filters accurately and clears cleanly. | `[ ] Pass` `[ ] Fail` |
| **QA-05.4** | P0 | Regular Fixed-Price Item Cart Operations | Add item, increment/decrement qty (`+`/`-`), remove item, add special instructions/note, clear cart. | Subtotal, VAT breakdown, and grand total recalculate accurately on every edit. | `[ ] Pass` `[ ] Fail` |
| **QA-05.5** | P0 | Weight-Based Product — Weight Mode | Select a weight-based product (e.g., Lechon ₱1,000/kg), enter Weight Mode `0.75 kg`, add to cart. | Line total computes exact `₱750.00`, cart badge preserves fractional precision (`0.75 kg`), never rounds to integer `1`. | `[ ] Pass` `[ ] Fail` |
| **QA-05.6** | P0 | Weight-Based Product — Amount Mode | Select weight-based product, switch to Amount Mode, enter `₱300.00`, add to cart. | Computes exact fractional weight (`0.3 kg` or `0.300 kg`) and exact line total `₱300.00` without penny drift. | `[ ] Pass` `[ ] Fail` |
| **QA-05.7** | P0 | Modifiers (Required / Min-Max / Multi-Select) | Select product with required modifier group and multi-select add-ons. Attempt to add without required selection, then exceed max selection. | Required modifier enforces selection; max selection prevented; paid add-ons add accurately to unit price & line total. | `[ ] Pass` `[ ] Fail` |
| **QA-05.8** | P0 | Complimentary Weight Modifiers & Notes | Add weight-based item qualifying for complimentary sets (linked & unlinked modifiers); edit item note. | Complimentary modifiers show `FREE` label when below/at threshold, charge when over threshold, do not collapse distinct notes, and deduct linked inventory on checkout. | `[ ] Pass` `[ ] Fail` |
| **QA-05.9** | P1 | Order Type & Service Mode Metadata | Switch between `DINE_IN` (enter Table # and Head Count), `TAKE_OUT`, and `DELIVERY`. | Order type and table/headcount save with order and print on receipt & prep ticket. | `[ ] Pass` `[ ] Fail` |

---

## Module 6 — Discounts & Non-Negative Total Enforcement (`DiscountModal`)

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-06.1** | P0 | Manual Percentage Valid Cases | Open **Apply Discount** -> select **Manual** -> **% Percent**. Test `0%`, `10%`, `12.5%`, `50%`, `100%`. | Discount computes accurately; at `100%`, final total is `₱0.00` and VAT is `₱0.00`. | `[ ] Pass` `[ ] Fail` |
| **QA-06.2** | P0 | Manual Percentage Extreme / Invalid Guard | Enter `150%`, `10000%`, and `-5%` in Manual Percentage and click **Apply**. | Validation rejects out-of-range input (`0%` to `100%`); **final transaction total NEVER goes negative (`>= ₱0.00`)**. | `[ ] Pass` `[ ] Fail` |
| **QA-06.3** | P0 | Manual Fixed Amount Valid & Over-Subtotal Guard | Select **Manual** -> **₱ Fixed**. Test `₱0.00`, `₱100.00`, exact cart subtotal, then enter an amount greater than subtotal (e.g., `₱99,999`) and `-50`. | Valid fixed amounts deduct cleanly; negative rejected; over-subtotal rejected or clamped so **final total is NEVER `< ₱0.00`**. | `[ ] Pass` `[ ] Fail` |
| **QA-06.4** | P0 | Statutory Discounts (SC / PWD / SP / BNPC) | Select Senior Citizen (`SC`) or `PWD`. Click **Apply** without ID, then enter valid Beneficiary ID and apply. | Blank ID shows validation error (`Beneficiary ID / Card Number` required). With valid ID, removes 12% VAT (VAT-Exempt Sale) and applies statutory 20% discount accurately. | `[ ] Pass` `[ ] Fail` |
| **QA-06.5** | P1 | Configured Promo / Corporate Discounts | Select store promotional `%` or fixed `₱` discount configured in `POS.Web`. | Applies configured rate/amount cleanly; cart updates dynamically if items are added/removed afterward (re-clamping if subtotal drops). | `[ ] Pass` `[ ] Fail` |
| **QA-06.6** | P1 | Remove Discount | With an active discount, reopen modal and click **Remove Discount**. | Restores full original subtotal, VAT, and total cleanly. | `[ ] Pass` `[ ] Fail` |

---

## Module 7 — Checkout, Payment Methods & Split Payments (`/new-sale`)

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-07.1** | P0 | Single Cash Payment | Click **Proceed to Payment**, select `CASH`, test under-tendered amount, then exact and over-tendered cash (and quick bill buttons). | Cash Tendered field is visible; under-tendered blocked; over-tendered computes exact Change; order saves with `PaymentStatus = PAID`, sequential BIR Invoice #, auto-prints receipt, and **opens cash drawer once**. | `[ ] Pass` `[ ] Fail` |
| **QA-07.2** | P0 | Single Non-Cash Payment (Card / GCash / Bank) | Complete a sale with `CARD`, then another with `GCASH` (Requires Reference). | **Cash Tendered input is hidden**; Reference Number is required for `GCASH`/`BANK`; order saves `PAID`, auto-prints receipt, and **cash drawer stays CLOSED**. | `[ ] Pass` `[ ] Fail` |
| **QA-07.3** | P0 | Split Payment (Cash + Card / GCash) | Pay a `₱500` order with `₱200 CASH` + `₱300 GCASH` (with reference). | Exact remaining balance validates to `₱0.00`; both payment legs save accurately; receipt prints split breakdown; **cash drawer opens ONCE** (because Cash leg exists). | `[ ] Pass` `[ ] Fail` |
| **QA-07.4** | P0 | Split Payment (Non-Cash Only: Card + GCash) | Pay a `₱500` order with `₱250 CARD` + `₱250 GCASH`. | Saves `PAID`, prints receipt, and **cash drawer stays CLOSED**. | `[ ] Pass` `[ ] Fail` |
| **QA-07.5** | P0 | 100% Discounted Zero-Total Sale (`₱0.00`) | Apply `100%` manual discount so Total Due = `₱0.00` and complete checkout. | Completes cleanly without negative total or payment validation deadlock; prints `₱0.00` receipt with BIR invoice #. | `[ ] Pass` `[ ] Fail` |
| **QA-07.6** | P0 | Double-Tap / Concurrency Checkout Protection | Rapidly double-click **Complete Payment** on a sale. | Only **one** order, **one** invoice number, **one** auto-print job, and **one** cash drawer kick occur. | `[ ] Pass` `[ ] Fail` |

---

## Module 8 — Held Sales & 4:00 AM Auto-Expiration (`/held-sales`)

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-08.1** | P0 | Hold Sale (Online & Offline) | Build a cart with weight item, modifiers, note, and discount. Click **Hold Sale**, enter customer name/notes, save. Repeat while offline. | Cart clears; held ticket appears on `/held-sales` with Hold Reference, item count, total, cashier, and Manila timestamp. | `[ ] Pass` `[ ] Fail` |
| **QA-08.2** | P0 | Resume Held Sale (With Empty & Non-Empty Cart) | Click **Resume** on `/held-sales`. Repeat when active cart already has items. | Non-empty cart warns before overwriting; resuming loads exact items, fractional weights, modifiers, notes, and discounts into `/new-sale` and removes ticket from active held list. | `[ ] Pass` `[ ] Fail` |
| **QA-08.3** | P1 | Cancel Held Sale | Click **Cancel** on an active held sale and confirm. | Ticket is removed from active held list and marked `CANCELLED` in audit log. | `[ ] Pass` `[ ] Fail` |
| **QA-08.4** | P0 | Held Sale Persistence Across Restart | Hold a sale offline, completely close and reopen `POS.Client`, log back in, and open `/held-sales`. | Held sale persists intact in SQLite and resumes cleanly. | `[ ] Pass` `[ ] Fail` |
| **QA-08.5** | P0 | 4:00 AM Business-Day Auto-Expiration | Have a held sale from Business Date `D` (e.g., created before `03:59 AM`); advance past `04:00 AM Asia/Manila` (next Business Date `D+1`) and open `/held-sales` or attempt direct resume. | Previous-day held sales are automatically expired/cancelled and cannot be resumed on the new business day; Z-Reading is not blocked by expired held sales. | `[ ] Pass` `[ ] Fail` |

---

## Module 9 — Transactions History, Reprint, Void & Sales Returns (`/transactions`)

> **QA Note:** Route `/returns` is currently a placeholder page (`Returns.razor`). Full **Sales Returns** and **Transaction Voids** are executed inside **`/transactions`** (`Transactions.razor`).

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-09.1** | P0 | Strict RBAC Denial for Plain Cashier | Log in as `Cashier A` and attempt to navigate directly to `/transactions` (both online & offline). | Access denied / blocked; cashier cannot view historical orders, void, or return. | `[ ] Pass` `[ ] Fail` |
| **QA-09.2** | P0 | Supervisor Access & 4:00 AM Cutoff | Log in as `Senior Cashier`, `Manager`, or `Admin` and open `/transactions`. | Lists current business day's orders (since `04:00 AM PHT`) for the terminal; filters by All / **Reprint Mode** / **Void Mode** and search by Invoice/Order # work accurately. | `[ ] Pass` `[ ] Fail` |
| **QA-09.3** | P0 | Receipt Reprint & Prep Ticket Reprint | Click **Reprint** on a paid row, and open **View** -> reprint preparation tickets. | Prints receipt clearly marked **`REPRINT`**; **NEVER opens cash drawer** and never duplicates original sale. | `[ ] Pass` `[ ] Fail` |
| **QA-09.4** | P0 | Online Void — Authorization & Validation | Select paid sale -> click **Void**. Test: (a) blank reason, (b) Cashier PIN, (c) Supervisor username typed as PIN, (d) valid Manager/Admin PIN. | Cases (a), (b), (c) rejected; case (d) succeeds, marks order `VOIDED`, records offsetting refund (Cash deducts drawer expected balance; Non-Cash flagged for manual refund), and restores product & linked modifier inventory. | `[ ] Pass` `[ ] Fail` |
| **QA-09.5** | P0 | Offline Void & Reconnect Sync | Disconnect internet, open `/transactions` as Supervisor, void a paid order with valid Supervisor PIN + reason, restart app, then reconnect. | Order displays `VOIDED` locally immediately, survives restart, uploads `Order -> VOID` in order upon reconnect, and syncs idempotently without duplicating. | `[ ] Pass` `[ ] Fail` |
| **QA-09.6** | P1 | Offline Void Conflict Handling (`409`) | Void order `X` online from another station/POS.Web while terminal is offline, then void the same order `X` offline on POS.Client and reconnect. | Server returns `409 Conflict` (prevents double-void); POS.Client flags `NEEDS_ATTENTION` / `CONFLICT` in outbox without silently un-voiding local audit trail. | `[ ] Pass` `[ ] Fail` |
| **QA-09.7** | P0 | Sales Return (Partial & Full) | Open **View** on a paid order -> click **Return**. Select item qty (`0`, negative, > purchased qty, valid partial qty), choose Restock Disposition (`RESTOCK` / `DAMAGED`), enter reason + Supervisor PIN. | Rejects `0`/invalid quantities and non-supervisor PINs; valid partial return updates payment/return status, refunds exact item amount, and applies chosen inventory disposition. | `[ ] Pass` `[ ] Fail` |

---

## Module 10 — Cash Drawer, Shift Lifecycle & Close Cashier (`/cash-drawer`, `/my-shift`)

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-10.1** | P0 | Open Shift & Opening Fund Inheritance | Open `/cash-drawer` with no active shift. Verify pre-filled/expected Opening Fund (Outlet Default vs Terminal Override) and enter opening denomination count. | Opening denomination UI lists bills & coins in **descending order** (highest to lowest), **excludes ₱20 bill**, and **hides per-denomination subtotals** while computing accurate total & opening discrepancy. Shift opens `OPEN`. | `[ ] Pass` `[ ] Fail` |
| **QA-10.2** | P0 | Cash In (`CASH_IN`) & Petty Cash / Cash Out (`PETTY_CASH` / `CASH_OUT`) | Post a `CASH_IN` (`₱500`), then attempt a `CASH_OUT` exceeding drawer balance, then post a valid `PETTY_CASH` requiring approval. | `CASH_IN` posts immediately (+`₱500` expected); overdraft `CASH_OUT` rejected (`400`); approval-required `PETTY_CASH` saves as `PENDING_APPROVAL` without altering balance until Supervisor PIN approves. | `[ ] Pass` `[ ] Fail` |
| **QA-10.3** | P1 | Safe Cash Drop (`CASH_DROP`), Reversal & Voucher Print | Record a `CASH_DROP` with Supervisor approval, print voucher, then reverse an eligible posted movement and attempt double-reversal. | Voucher prints with `PCV`/`CIN`/`COU`/`CDP`/`REV` reference; reversal restores expected balance; double-reversal is blocked; cash movements never alter BIR Gross Sales or VAT. | `[ ] Pass` `[ ] Fail` |
| **QA-10.4** | P0 | Manual Open Cash Drawer Authorization | Click **Open Cash Drawer** as Cashier (without PIN, then with Supervisor PIN + reason), and as Supervisor directly. | Cashier without Supervisor PIN is denied; Cashier with valid Supervisor PIN + mandatory reason opens drawer and writes audit log; Supervisor opens with reason and audit log. | `[ ] Pass` `[ ] Fail` |
| **QA-10.5** | P0 | Close Cashier — UI & Denomination Breakdown | Open **Close Cashier** tab on `/cash-drawer` or `/my-shift`. Inspect denomination table and enter counts + non-cash payment declarations. | Bills & coins ordered **descending**, **NO ₱20 bill**, **NO individual denomination subtotals** shown to cashier; blind count calculates accurate total, expected cash, actual cash, shortage/overage, and saves denomination JSON. | `[ ] Pass` `[ ] Fail` |
| **QA-10.6** | P0 | Close Cashier — Blocked While Pending Movement Exists | Leave a `PENDING_APPROVAL` cash movement unresolved and try to submit Close Cashier. | Blocked with clear error until pending movement is approved or rejected. | `[ ] Pass` `[ ] Fail` |
| **QA-10.7** | P0 | Close Cashier — Declaration Print, Logout & Cashier-Only Restriction | Complete Close Cashier as Cashier A on Terminal 1. Then try: (a) Cashier A login on Terminal 1, (b) Cashier A login on Terminal 2, (c) Cashier B login on Terminal 1. | Declaration Acknowledgment prints (reprintable without duplicating); Cashier A is signed out; **(a) Blocked** with cashier-level restriction (NOT Z-Reading terminal lock!); **(b) Allowed** on Terminal 2; **(c) Allowed** for Cashier B on Terminal 1. Verify `Terminals.ClosedBusinessDate` remains `NULL`! | `[ ] Pass` `[ ] Fail` |

---

## Module 11 — Anytime X-Reading, End-of-Day Z-Reading & Supervisor Reopen (`/z-reading`)

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-11.1** | P0 | Supervisor-Only Route Access | Attempt to open `/z-reading` as plain `Cashier`. | Blocked / hidden from navigation. | `[ ] Pass` `[ ] Fail` |
| **QA-11.2** | P0 | Anytime Read-Only X-Reading (While Shifts Open) | With Cashier A closed and Cashier B's shift still `OPEN` on Terminal 1, open `/z-reading` -> **X-Reading** tab and run X-Reading twice (online & offline). | Generates accurate interim audit totals across shifts while shift stays `OPEN`; running twice produces **zero** database mutations, **zero** locks, and **zero** Z-Counter increments; prints cleanly. | `[ ] Pass` `[ ] Fail` |
| **QA-11.3** | P0 | Z-Reading Readiness Blockers | Open `/z-reading` -> **Z-Reading** tab while: (a) a shift is `OPEN`, (b) a held sale is active, (c) outbox has pending/failed items, or (d) `ZReadingTerminalStates` is unprovisioned. | Readiness panel lists every active blocker accurately and disables **Generate & Finalize Z-Reading**. Verify `"Device identity is missing"` does **NOT** appear on UUID-activated terminals (`v1.0.4`). | `[ ] Pass` `[ ] Fail` |
| **QA-11.4** | P0 | Z-Reading Same-Day Finalization & Atomic Terminal Lock | Resolve all blockers (all shifts closed, outbox synced), tick confirmation checkbox, click **Generate & Finalize Z-Reading**. | Generates immutable Z-Reading (`Z #N`), increments Accumulated Grand Total, locks **only Terminal 1** for current Business Date until `04:00 AM Asia/Manila` (Terminal 2 remains usable), and shows preview + **Print / Reprint** + **Lock Terminal & Sign Out**. | `[ ] Pass` `[ ] Fail` |
| **QA-11.5** | P0 | Z-Reading Print Audit & Reprint Labeling | Print original Z-Reading with mandatory reason, then request a reprint with reason `"Supervisor copy"`. | Both attempts append to Print Audit list; reprint is explicitly marked `REPRINT` and never creates a second Z-Reading record. | `[ ] Pass` `[ ] Fail` |
| **QA-11.6** | P0 | Terminal Locked Screen (`/terminal-locked`) & 4:00 AM Auto-Unlock | Click **Lock Terminal & Sign Out** after Z-Reading; restart `POS.Client`; try logging in as Cashier A, Cashier B, and Manager before `04:00 AM PHT`. | Redirects to `/terminal-locked` showing Z-Reading lock until `04:00 AM (Asia/Manila)`; persists across restart; automatically unlocks at `04:00 AM PHT` next business day. | `[ ] Pass` `[ ] Fail` |
| **QA-11.7** | P0 | Supervisor Reopen Closed Cashier | After Cashier A performs Close Cashier (before Z-Reading), open `/z-reading` as Supervisor, enter mandatory Reopen Reason, and click **Reopen** for Cashier A. | Marks restriction `REOPENED` with audit metadata, preserves original closed shift immutably, and permits Cashier A to log back in and open a new shift on Terminal 1 (works online & offline). | `[ ] Pass` `[ ] Fail` |

---

## Module 12 — Unified ESC/POS Printer, Auto-Print & Cash Drawer Hardware (`/printer-settings`)

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-12.1** | P0 | Ethernet/LAN Printer Setup & Test | Open `/printer-settings`, choose **Ethernet / LAN**, enter Printer IP + Port `9100`, save, and click **Test Printer** & **Test Cash Drawer**. | Test slip prints cleanly with zero sale required; cash drawer kicks via `1B 70 ...`; config persists across app restart. | `[ ] Pass` `[ ] Fail` |
| **QA-12.2** | P0 | USB / OTG Printer Discovery & Permission (Windows & Android) | Switch to **USB / OTG Printer**, click **Discover USB Printers**, select printer, click **Grant USB Permission** (on Android), save, and run **Test Printer** & **Test Cash Drawer**. | Detects raw USB printer (`usbprint.sys` on Windows / USB Host on Android); grants runtime USB permission on Android; prints and kicks drawer cleanly; recovers gracefully after USB cable unplug & replug. | `[ ] Pass` `[ ] Fail` |
| **QA-12.3** | P1 | Paper Width (`80mm` vs `58mm`), Density, Encoding, Auto-Cut & Raster Logo | Test receipts on both `80mm` (48 cols) and `58mm` (32 cols) with logo enabled, Branch Tagline set, and VAT Display toggled On/Off. | Long product names wrap cleanly without clipping; raster logo (`GS v 0`) prints sharply; footer shows centered **`AGRES Business Solution`**; **no `BranchCode`** on customer receipt; VAT lines hide when `VatDisplay = false` while tax math stays intact. | `[ ] Pass` `[ ] Fail` |
| **QA-12.4** | P0 | Station Routing (Kitchen vs Bar Prep Tickets) | Complete a mixed sale containing 1 Kitchen item (e.g., Lechon + Atchara) and 1 Bar item (e.g., Drink). | Splits Kitchen prep ticket and Bar prep ticket accurately with item modifiers and special instructions. | `[ ] Pass` `[ ] Fail` |
| **QA-12.5** | P0 | Printer Disconnected Resilience & Controlled Retry | Unplug/power off thermal printer, then finalize a Cash sale on `/new-sale`. Reconnect printer and trigger retry/reprint. | Sale still saves as `PAID` without crashing or hanging UI; clear print warning shown; printer reconnect allows controlled reprint without duplicating order or re-kicking drawer unexpectedly. | `[ ] Pass` `[ ] Fail` |

---

## Module 13 — Terminal Licensing & Anti-Tamper Security (`/license-info`)

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-13.1** | P0 | License Details & Online Validation | Open `/license-info`, verify displayed Branch/Outlet/Terminal, Hardware Device ID, License Key, Granted Duration, Expiration Date, Remaining Days, and Offline Grace Period; click **Validate License Online**. | All fields match `POS.Web` AGRES Licensing records; validation updates Last Successful Server Validation timestamp cleanly. | `[ ] Pass` `[ ] Fail` |
| **QA-13.2** | P1 | Expiring Soon Warnings (90 / 60 / 30 / 7 Days) | Set a UAT test license expiring within 30 days (or 7 days), validate online, and complete a sale. | Displays yellow expiration warning banner; sales and opening shifts remain **allowed**. | `[ ] Pass` `[ ] Fail` |
| **QA-13.3** | P0 | Expired / Suspended / Revoked Enforcement | In `POS.Web` (as AGRES System Admin), suspend or revoke (or expire) Terminal 1's license -> click **Validate License Online** on POS.Client -> try opening shift & making a sale. | Red restriction banner shown; **new sales and new shifts are blocked**; historical `/transactions` inquiry, receipt reprints, and `/z-reading` archive remain accessible. Renewing/reactivating in `POS.Web` + re-validating restores sales. | `[ ] Pass` `[ ] Fail` |
| **QA-13.4** | P0 | Anti-Tamper: Clock Rollback & DB Copy | (a) Roll device clock backward past last known server time. (b) Copy `local_pos.db` onto a different Windows PC / Android tablet and launch. | (a) Detects clock rollback while preserving transaction integrity. (b) Rejects copied SQLite license binding (hardware fingerprint mismatch). | `[ ] Pass` `[ ] Fail` |

---

## Module 14 — Offline Continuity, Background Sync & Conflict Recovery

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-14.1** | P0 | Full Offline Shift Lifecycle | Disconnect Wi-Fi/LAN completely. Perform: PIN login -> Open Shift -> Cash Sale -> Split Sale (weight + modifiers + discount) -> Hold & Resume Sale -> Petty Cash Out -> Offline Void (with Supervisor PIN) -> Offline X-Reading -> Offline Close Cashier. | Every operation completes smoothly without timeouts or crashes; local receipt/voucher/declaration printing works via direct ESC/POS without `POS.Api`. | `[ ] Pass` `[ ] Fail` |
| **QA-14.2** | P0 | App Kill & Cold Restart While Offline | Force-close `POS.Client` while offline with an open shift, held sale, and queued outbox items; relaunch while still offline. | Open shift, held sale, queued outbox items, invoice sequence counter, and closing restrictions all survive intact. | `[ ] Pass` `[ ] Fail` |
| **QA-14.3** | P0 | Reconnect Priority Sync & Idempotency | Restore internet and wait for background sync (or click **Sync Now**). Verify `POS.Web` & PostgreSQL. | Syncs cleanly in dependency order (`SHIFT_OPEN` -> `Order` -> `MOVEMENT` -> `VOID` -> `SHIFT_CLOSE` -> `CASHIER_REOPEN`); zero duplicate orders, shifts, or voids; BIR invoice numbers match local receipts exactly. | `[ ] Pass` `[ ] Fail` |
| **QA-14.4** | P1 | Sync Retry Backoff & Failed Item Visibility | Simulate transient 500/network drop during sync, then restore. | Retries with exponential backoff and completes without data loss; permanent conflicts (`409`) move to `NEEDS_ATTENTION` and display in header/Dashboard without blocking healthy items. | `[ ] Pass` `[ ] Fail` |

---

## Module 15 — Platform Compatibility, Upgrades & Security Negative Tests

| ID | Priority | Scenario | Steps | Expected Result | Status |
|---|---|---|---|---|---|
| **QA-15.1** | P0 | In-Place Upgrade Preservation (Windows & Android) | Install `v1.0.3`, create an unsynced offline order & configure printer settings, then install `v1.0.4` over `v1.0.3` (without uninstalling). | Upgrade succeeds in-place (matching Android package ID `com.agres.pos.client` & signing certificate); `local_pos.db`, `DeviceUuid`, license, printer settings, and unsynced orders are 100% preserved. | `[ ] Pass` `[ ] Fail` |
| **QA-15.2** | P1 | Responsive UI Check (Windows Desktop & Android Tablet) | Test `/login`, `/new-sale`, `DiscountModal`, `CashDrawer`, `/held-sales`, `/transactions`, `/z-reading`, `/printer-settings`, Switch Cashier & Logout modals on both 1080p Windows and Android tablet (landscape & portrait). | No clipped buttons, overlapping text, broken scroll containers, or unreachable modal action buttons. | `[ ] Pass` `[ ] Fail` |
| **QA-15.3** | P0 | AGRES Platform vs Client Security Boundary | Attempt to: (a) log in to `POS.Client` using AGRES System Admin (`admin`), (b) sync AGRES account into `LocalCashiers`, (c) open `/transactions` or `/z-reading` directly by URL as Cashier, (d) authenticate a User belonging to Organization B on Organization A's terminal. | All 4 attempts are strictly rejected on both server API and local SQLite; zero privilege escalation possible. | `[ ] Pass` `[ ] Fail` |

---

## Final Release Gate Sign-Off

- [ ] **All P0 (Blocker) test cases:** 100% PASS
- [ ] **All P1 (High) test cases:** 100% PASS (or documented waiver)
- [ ] **Windows x64 (`POS_Client_Setup.exe`) verified:** `[ ] Yes`
- [ ] **Android APK (`com.agres.pos.client-v1.0.4-universal-signed.apk`) verified:** `[ ] Yes`
- [ ] **Offline-to-Online sync reconciled against `POS.Web` & PostgreSQL:** `[ ] Yes`

**QA Lead Signature:** `_________________________` &nbsp;&nbsp; **Date:** `________________`

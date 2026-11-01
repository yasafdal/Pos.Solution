# Security Hardening Test Results

**Date:** 2026-11-01  
**Execution Environment:** .NET 10.0 / Windows 11 x64  
**Executed By:** Automated Test Runner (`POS.Tests`, `POS.Client.Tests`, Python Automation Suites)  

---

## 1. Automated Security Verification Suite (`POS.Tests`)

Command executed:
```bash
dotnet run --project POS.Tests/POS.Tests.csproj
```

### Results:

| Test ID | Scenario Description | Expected Outcome | Actual Result | Status |
|---|---|---|---|---|
| **SEC01** | Controller authorization and deny-by-default reflection scan | All controllers have `[Authorize]` except public `AuthController`; `EndpointsController` is strictly Admin-only | Verified via reflection across all 32 controllers | **PASS** |
| **SEC02** | Privilege escalation prevention | `Manager` role cannot create an `Admin` user; `Manager` cannot delete an `Admin` account | HTTP 403 `ForbidResult` returned for unauthorized escalation attempts | **PASS** |
| **SEC03** | Sensitive data masking (non-admin PIN redaction) | Non-admin caller receives masked PIN `****`; Admin receives unmasked PIN | Non-admin response received `****`; Admin response received unmasked value | **PASS** |
| **SEC04** | Cart item price and quantity tampering prevention | Negative item quantities, negative base prices, and negative modifier prices are rejected | HTTP 400 `BadRequestObjectResult` with validation message | **PASS** |
| **SEC05** | BOLA / IDOR cross-branch order, void, and return protection | Caller from Branch 1 is rejected when attempting to view, void, or return orders belonging to Branch 2 | HTTP 403 `ForbidResult` on all cross-branch operations | **PASS** |
| **SEC06** | BOLA / IDOR manager cross-branch hierarchy protection | Manager from Branch 1 cannot create terminals or outlets assigned to Branch 2 | HTTP 403 `ForbidResult` on cross-branch hierarchy mutations | **PASS** |
| **SEC07** | File upload size bounding (5MB max) | Uploading files larger than 5MB to `/api/Products/upload-image` is rejected | HTTP 400 `BadRequestObjectResult` with "exceeds the 5MB limit" message | **PASS** |

---

## 2. Full Regression Test Suites

### 2.1 Backend Core Suite (`POS.Tests`)
- **Total Scenarios:** 63
- **Passed:** 63
- **Failed:** 0
- **Categories Covered:**
  - Cash movements & petty cash lifecycle (Scenarios 01–18)
  - Token claims scoping & business day cutoffs (Scenarios 19–22)
  - Outlet & terminal ownership, IP collision rejection (Scenarios 23–37)
  - Thermal receipt layout & syntax integrity (Scenario 38)
  - Opening float enforcement (Scenario 39)
  - Kitchen & Bar ticket routing (Scenario 40)
  - Denomination counting & float separation (Scenarios 41–48)
  - Z-Reading period boundaries, immutability, reprint audit (Scenarios Z01–Z08)
  - Security hardening (Scenarios SEC01–SEC07)

### 2.2 Client Isolation Suite (`POS.Client.Tests`)
Command executed:
```bash
dotnet run --project POS.Client.Tests/POS.Client.Tests.csproj --no-restore
```
- **Total Scenarios:** 9
- **Passed:** 9
- **Failed:** 0
- **Verifications:**
  - Scratch path seam cannot access user SQLite
  - Real local opening, offline checkout, retry and process restart
  - Blank/negative opening and wrong terminal/branch rejected
  - Checkout cannot bypass authenticated cashier/terminal
  - Replay owner, exact pending cash, and placeholder reconciliation
  - Z-reading local readiness blockers
  - EF Core model snapshot drift check (Zero drift confirmed)

### 2.3 Hardware & Python Static Acceptance
Commands executed:
```bash
python POS.Tests/verify_receipt_printer.py
python POS.Tests/verify_z_reading_printer.py
python POS.Tests/verify_z_reading_guards.py
python POS.Tests/verify_z_reading_ui.py
python scripts/verify-z-reading-ui-layout.py
```
- **verify_receipt_printer.py:** 6 passed
- **verify_z_reading_printer.py:** 2 passed
- **verify_z_reading_guards.py:** 1 passed
- **verify_z_reading_ui.py:** 3 passed
- **verify-z-reading-ui-layout.py:** 3 viewports passed (desktop, tablet, mobile)

---

## 3. Evidence Log

```
=== Summary: 63 passed, 0 failed in POS.Tests ===
=== Summary: 9 passed, 0 failed in POS.Client.Tests ===
=== All Python acceptance and layout tests passed ===
Build succeeded: 0 errors across POS.Api, POS.Client, and POS.Web.
Git diff check: Clean (0 whitespace/formatting errors).
```

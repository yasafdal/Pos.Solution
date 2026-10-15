# Transaction Sales History & Sales Reporting Architecture (`docs/TRANSACTION_HISTORY_ARCHITECTURE.md`)

## 1. Existing Architecture Summary
- **Orders & OrderItems:** Persist complete financial snapshots (`Subtotal`, `DiscountAmount`, `TotalAmount`, `VatableSales`, `VatExemptSales`, `ZeroRatedSales`, `VatAmount`, `DiscountCode`, `CustomerCardNumber`, `InvoiceNumber`, `InvoiceIssuedAt`, `CustomerName`, `CustomerTin`).
- **Payments & PaymentMethods:** Store successful tenders (`PaymentMethod`, `Amount`, `AmountTendered`, `ChangeAmount`, `ReferenceNumber`, `PaymentStatus`).
- **Branch & Outlet Association:** Every order references `BranchId` and `OutletId`, allowing multi-branch and multi-terminal sales segregation.
- **Invoice Integrity:** Invoice numbers are generated sequentially via `InvoiceSequences` upon payment finalization and remain immutable on reprinting.

## 2. Gaps & Requirements for Transaction Sales History & Reporting
- **Backend API Endpoints:** Need dedicated endpoints in `POS.Api` for searching sales history with date ranges, branch/outlet filters, cashier filters, payment method filters, and pagination.
- **Reporting Services:** Need aggregated reporting endpoints (Daily Sales Summary, Z-Reading / Shift Reports, Product Sales Breakdown, VAT & Statutory Discount summaries).
- **Invoice Reprinting Safety:** Ensure reprinting endpoint `GET /api/orders/{id}/receipt?reprint=true` explicitly sets the reprint flag, records an audit log if required, and **never** mutates totals, increments invoice numbers, or deducts inventory twice.
- **Admin & Cashier UI (`POS.Web` / `POS.Client`):** Responsive transaction lookup table with date filters, order detail drawer/modal, and secure controlled invoice reprinting.

## 3. Proposed Implementation Plan
- **Layer 2:** Database indexes & Query optimization for sales history filtering.
- **Layer 3:** Sales History & Reporting API Controllers (`SalesController.cs`).
- **Layer 4:** `POS.Web` Sales History & Reporting Admin Dashboard (`SalesHistory.razor`, `SalesReports.razor`).
- **Layer 5:** POS Client Reprint & Transaction Lookup Integration.
- **Layer 6:** Comprehensive Test Suite & Final Audit Report.

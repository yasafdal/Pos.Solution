# DISCOUNT ARCHITECTURE ANALYSIS & DESIGN (`docs/DISCOUNT_ARCHITECTURE.md`)

## 1. Existing Architecture Summary
- **POS.Api:** ASP.NET Core Web API with PostgreSQL (`PosDbContext`), supporting robust transactional orders (`OrdersController`), payments (`PaymentsController`), product management (`ProductsController`), and tax/branch lookups.
- **POS.Client:** .NET MAUI Blazor Hybrid cashier terminal featuring `NewSale.razor`, real-time `CartService`, modifier customization, and receipt preview/printing.
- **POS.Web:** Blazor Web admin portal supporting product management, category setup, and branch administration.
- **POS.Shared:** Shared DTOs and interfaces used across client and API.

## 2. Proposed Service Structure
- **`IDiscountService` / `DiscountService` (Shared/Api):** Core domain service handling statutory discount calculations (SC/PWD 20% + VAT exemption, BNPC 5%, promotional, and employee discounts), eligibility validation, double-discount prevention, and subtotal/VAT breakdown calculations.
- **Client State Integration:** Extend `CartService` in `POS.Client` to support active discount application (SC/PWD ID capture, customer name/TIN, and discount type) and real-time re-calculation of net totals.

## 3. Existing Reusable Components
- `CartService`: Manages cart items, modifier calculations, and reactive `OnChange` events.
- Modal Dialog components in `NewSale.razor`: Reusable modal structure for payment and checkout interactions (can be extended for Discount Application modal).
- `PaymentProcessDto` / `OrderCreateDto`: Already contain extensible fields for customer data (`CustomerName`, `CustomerTin`, etc.).

## 4. Required Database Changes
- **`Orders` Table Enhancements:** Add columns to record applied discount metadata securely:
  - `DiscountType` (`varchar(30)`, e.g., `NONE`, `SC`, `PWD`, `BNPC`, `PROMO`, `EMPLOYEE`)
  - `DiscountRate` (`decimal(5,2)`, e.g., `20.00`, `5.00`)
  - `CustomerCardNumber` (`varchar(50)`, e.g., SC/PWD ID number)
- **Manual Idempotent SQL Script:** `POS.Api/Database/DiscountModule.sql` (following project conventions).

## 5. API Changes
- **`OrdersController` & `PaymentsController`:** Validate applied discount calculations server-side against authoritative database prices and tax flags before saving. Never trust client-supplied discount totals.
- **Discount Validation Endpoint / Service:** Provide backend validation ensuring SC/PWD ID format, eligibility, and exclusion of VAT for statutory discounts.

## 6. POS.Web Changes
- **Admin Settings / Configuration:** Optional configuration for promo/employee discount thresholds or authorized discount types.

## 7. POS.Client Changes
- **Discount Modal in `NewSale.razor`:** Add an intuitive touch-optimized **Apply Discount** button and modal allowing cashiers to select discount type, enter SC/PWD ID and Customer Name, and instantly view VAT-exempt and net discount breakdowns.
- **Cart Summary Update:** Clearly display applied discount name, discounted amount, VAT exemption savings, and final amount due.

## 8. Receipt Changes
- **`ReceiptDto` & `ReceiptHtmlFormatter`:** Update receipt templates to prominently display:
  - Gross Sales / VATable Sales / VAT-Exempt Sales / Zero-Rated Sales breakdown.
  - Applied Discount type and amount (e.g., *Senior Citizen Discount (20%): ₱200.00*).
  - VAT Exemption amount.
  - Net Payable.

## 9. Data Migration Risks & Compatibility Considerations
- **Backward Compatibility:** Existing orders without discounts default to `DiscountType = 'NONE'`, `DiscountAmount = 0.00`, preserving historical order records without breaking past transactions.
- **PostgreSQL Case Sensitivity:** Explicit mapping via `HasColumnName` in EF Core fluent configuration.
- **No Automated Migrations:** Apply schema changes via manual idempotent SQL scripts.

## 10. Restrictions
- Preserve existing payment flows, branch routing, and invoice sequence generation.
- No third-party packages or unnecessary abstractions.

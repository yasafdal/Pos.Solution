# Task: Implement Product Short Code with Automatic Suggestions and Receipt Integration

**Important: Inspect the existing codebase before making changes. Do not recreate existing functionality.**

## 1. Objective

Add a new column named `ShortCode` to the existing `Products` table.

The short code will serve as an abbreviated product name primarily used for:

- Thermal receipt printing (58mm / 80mm).
- Compact order summaries.
- Kitchen Order Tickets (future implementation).
- Other POS screens where the complete product name is too long.

Example:

| Product Name | Suggested Short Code |
|---|---|
| Iced Cafe Latte | ICE LATTE |
| Hot Cappuccino | HOT CAP |
| Matcha Milk Tea | MATCHA MT |
| Blueberry Muffin | BLUE MUFF |
| Ham & Cheese Sandwich | HAM CHZ SW |
| Caramel Syrup Pump | CARAMEL SYR |
| Extra Pearls / Sinkers | XTRA PEARLS |
| Tapsilog Meal | TAPSILOG |

These are suggested abbreviations, not mandatory exact outputs.

## 2. Database Changes

Update the existing Products entity and PostgreSQL table.

Add:

```csharp
public string? ShortCode { get; set; }
```

Database requirements:

- Column name should follow existing PostgreSQL naming conventions.
- Use a maximum length of 30 characters.
- Allow NULL initially for backward compatibility.
- Preserve existing product records.
- Create an EF Core migration using the existing DbContext.
- Do not recreate or drop the Products table.
- Do not modify unrelated columns or constraints.

Short codes do not need to be globally unique because they are display names, not product identifiers.

Continue using ProductId, SKU, and Barcode for their existing purposes.

## 3. Automatic Short Code Suggestions

In Product Management, whenever the administrator types or changes the Product Name, automatically generate a suggested short code.

Examples:

Product Name: `Iced Cafe Latte`

Suggested Short Code: `ICE LATTE`

Product Name: `Blueberry Muffin`

Suggested Short Code: `BLUE MUFF`

Requirements:

1. Generate suggestions based on the product name.
2. Convert suggestions to uppercase.
3. Remove unnecessary punctuation and excessive whitespace.
4. Use recognizable abbreviations where possible.
5. Limit suggestions to 30 characters.
6. Do not generate random values or database IDs.
7. Allow administrators to manually edit the suggested short code.
8. Never overwrite a manually edited short code when the Product Name changes.
9. If the ShortCode field is empty, generate a suggestion automatically.
10. The short code must not affect product identification or pricing.

Use a deterministic, reusable C# helper/service for the suggestion algorithm rather than calling an external AI service.

It is acceptable to implement a configurable abbreviation dictionary for common FNB and Retail words.

For example:

- ICED → ICE
- CAPPUCCINO → CAP
- BLUEBERRY → BLUE
- CHEESE → CHZ
- SANDWICH → SW
- SYRUP → SYR
- MILK TEA → MT
- EXTRA → XTRA

Preserve important words when abbreviation would make the product difficult to recognize.

## 4. Product Management UI

Modify the existing Product Create and Edit forms in the Blazor Web App.

Add a field:

**Short Code**

Position it near the Product Name field.

Suggested design:

Product Name:
`Iced Cafe Latte`

Short Code:
`ICE LATTE`

Display helper text:

"Suggested automatically based on the product name. You can edit this for receipt printing."

Behavior:

- On Create, populate the Short Code automatically when the Product Name changes.
- On Edit, load and preserve the existing ShortCode.
- If the user manually edits ShortCode, stop automatic overwriting.
- Provide a small Regenerate button beside the field to regenerate a suggestion from the current product name.
- Allow the user to override the suggestion.
- Validate the maximum length.
- Save the final value through the existing Product Create/Update APIs.

Do not create another Product Management page.

Also add the Short Code column to the existing Product Management table if it fits the current layout.

## 5. Backend API Changes

Update the existing Product DTOs and mappings to include ShortCode.

Check and modify the appropriate existing:

- CreateProductRequest
- UpdateProductRequest
- ProductResponse
- Product entity
- ProductService
- ProductController

Use their actual names in the solution.

Requirements:

- Accept ShortCode during product creation.
- Accept ShortCode during product updates.
- Return ShortCode when retrieving products.
- Trim and normalize short codes consistently.
- Validate the 30-character maximum.
- Preserve manually entered values.

Implement fallback behavior for products that do not have a ShortCode.

Do not create duplicate Product CRUD endpoints.

## 6. Receipt Printing Integration

Inspect the current receipt generation and printing implementation.

Update receipt item display to use:

```csharp
var displayName = !string.IsNullOrWhiteSpace(product.ShortCode)
    ? product.ShortCode
    : product.Name;
```

Requirements:

- Use ShortCode as the displayed product name on thermal receipts.
- Fall back to the original product name when ShortCode is NULL or empty.
- Keep the full ProductName available for reporting and transaction history.
- Do not modify product prices, quantities, modifiers, taxes, or payment calculations.
- Keep the receipt layout compatible with 58mm and 80mm thermal printing.
- Ensure long short codes still wrap or truncate safely without breaking price alignment.

### Important: Preserve Historical Receipts

Inspect how OrderItems currently stores product names and prices.

When creating an order, store both the original product name and its ShortCode snapshot.

If the existing design already supports name snapshots, extend it safely.

Suggested additional OrderItems field:

```csharp
public string? ProductShortCode { get; set; }
```

During order creation:

```csharp
orderItem.ProductName = product.Name;
orderItem.ProductShortCode = product.ShortCode;
```

During receipt generation:

```csharp
var receiptName = !string.IsNullOrWhiteSpace(orderItem.ProductShortCode)
    ? orderItem.ProductShortCode
    : orderItem.ProductName;
```

Do not retrieve the latest Product.ShortCode when reprinting historical invoices.

The original receipt display must remain consistent even if the administrator changes the product short code later.

For historical orders created before this feature, fall back to their saved ProductName.

## 7. Cashier POS Integration

Inspect the existing Blazor Hybrid cashier interface.

Requirements:

- Keep the full Product Name on the main product cards unless the current layout specifically benefits from a shorter display.
- Use ShortCode in receipt preview and printing.
- Consider using ShortCode in compact cart summaries, while preserving full product name in accessible product details.
- Ensure products remain searchable by their full name, SKU, and barcode.
- Optionally include ShortCode in product search.

Do not change the existing add-to-cart, modifiers, or payment functionality.

## 8. Suggested Project Structure

Follow the existing project conventions. Add a reusable helper or service such as:

`ProductShortCodeGenerator.cs`

Suggested method:

```csharp
public string GenerateShortCode(string productName)
```

The generator should:

- Normalize whitespace.
- Handle punctuation safely.
- Apply recognizable abbreviations.
- Respect the 30-character limit.
- Return a deterministic result.
- Handle empty or null product names safely.

Avoid unnecessarily introducing new dependencies.

## 9. Testing Requirements

Test the following scenarios:

1. Creating `Iced Cafe Latte` generates a suggested short code.
2. Manually editing the short code preserves the administrator's value.
3. Changing the product name before manual editing updates the suggestion.
4. Clicking Regenerate replaces the current short code with a new suggestion.
5. Updating an existing product preserves its stored short code.
6. Products without a short code display the full product name.
7. A receipt displays the saved short code.
8. A historical receipt retains its original short code even after the product is renamed.
9. Modifier selections still appear correctly on the receipt.
10. The ShortCode length cannot exceed 30 characters.
11. Full product name search continues to work.
12. Existing Retail and FNB products continue working normally.

## 10. Implementation Instructions

Before coding:

1. Inspect the existing Products entity and PostgreSQL schema.
2. Identify the Product Management Create/Edit components.
3. Inspect the ProductService and DTOs.
4. Inspect OrderItems and receipt printing implementation.
5. Summarize the proposed code and database changes.

Then implement the feature directly in the existing codebase.

Constraints:

- Do not recreate existing tables.
- Do not replace existing CRUD functionality.
- Do not modify unrelated modules.
- Use EF Core migrations with Npgsql.
- Follow the existing project architecture.
- Maintain backward compatibility.
- Keep the original ProductName unchanged.
- Do not affect successful payment processing.

After implementation, provide:

- Files created or modified.
- Database migration details.
- Short code generation logic.
- Receipt printing changes.
- Test results.
- Any assumptions or remaining issues.

The final result should automatically suggest a recognizable short code in Product Management and consistently use that saved short code in receipt printing while preserving historical transaction records.
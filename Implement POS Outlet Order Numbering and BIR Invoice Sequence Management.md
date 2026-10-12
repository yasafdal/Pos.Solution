# Task: Implement POS Outlet Order Numbering and BIR Invoice Sequence Management

**Important:** In my architecture, an `Outlet` represents a physical POS terminal/register. Do not create another POSTerminals table.

## Existing Outlet Table

The Outlet table already contains these columns:

- `OutletId`
- `BranchId`
- `Name`
- `DeviceIdentifier`
- `IsActive`
- `PTUNumber`
- `MINNumber`
- `SerialNumber`

Inspect the actual PostgreSQL schema and EF Core entity to confirm exact names, types, and constraints before making changes.

Preserve existing fields, relationships, and functionality.

## Objective

Implement two separate numbering systems:

1. **Order Number** — Internal POS transaction tracking, with an independent sequence per Outlet.
2. **Invoice Number** — Sequential sales invoice numbering using a configurable invoice series assigned to an Outlet, designed to support Philippine BIR requirements.

Do not assume that sequential numbering alone makes the POS BIR-compliant. The actual starting numbers, allocated series, terminal registration, and permit requirements must be configurable according to the merchant's BIR-authorized setup.

---

## 1. Order Number Sequence

Each Outlet must maintain its own internal order sequence.

Example:

| Outlet | Order Number |
|---|---|
| POS 01 | POS01-000001 |
| POS 01 | POS01-000002 |
| POS 02 | POS02-000001 |
| POS 02 | POS02-000002 |

Requirements:

- Generate numbers automatically from the backend.
- Maintain independent counters per Outlet.
- Use an immutable or controlled unique OutletCode/prefix rather than the editable Outlet Name.
- Never use MAX(OrderNumber) + 1.
- Prevent duplicate numbers during simultaneous transactions.
- Do not reset the counter daily, monthly, or yearly.
- Store generated OrderNumber in the existing Orders table.
- Do not generate a new number when editing an existing order.
- Retain numbers for cancelled orders as part of transaction history.

Create an `OrderSequences` table if necessary.

Suggested fields:

- `OrderSequenceId`
- `OutletId`
- `LastIssuedNumber`
- `CreatedAt`
- `UpdatedAt`

Use PostgreSQL transaction-safe row locking or an atomic UPDATE ... RETURNING implementation.

## 2. Invoice Number Sequence Management

Create an `InvoiceSequences` table for the BIR invoice numbering configuration.

Suggested fields:

- `InvoiceSequenceId`
- `OutletId`
- `SeriesCode`
- `Prefix`
- `StartingNumber`
- `EndingNumber` (nullable)
- `LastIssuedNumber`
- `PaddingLength`
- `IsActive`
- `CreatedAt`
- `UpdatedAt`

Requirements:

- Each series must have a configurable starting number.
- Support a configured ending number where applicable.
- Support zero-padded display numbers.
- Support an optional authorized prefix.
- Only one active invoice series per Outlet initially, unless the existing approved configuration requires a different design.
- Preserve historical invoice sequences when a new series is configured.
- Do not delete or overwrite previously issued invoice records.
- Check whether the configured ending number has been reached.
- Reject invoice issuance if no valid series is configured.
- Prevent duplicate invoice numbers within the appropriate invoice series.
- Do not automatically reset invoice numbers.

Example development configuration:

Outlet: POS 01

- StartingNumber: 1
- LastIssuedNumber: 0
- PaddingLength: 7
- Next InvoiceNumber: 0000001

Outlet: POS 02

- StartingNumber: 100001
- LastIssuedNumber: 100000
- PaddingLength: 7
- Next InvoiceNumber: 0100001

These are example allocations only, not prescribed or automatically approved BIR serial ranges.

Ensure the data model can be adapted if the authorized numbering scope is shared across multiple Outlets rather than strictly per terminal.

## 3. Update Existing Orders

Inspect the current Orders entity and table.

Reuse existing fields where possible. Add fields only when missing, such as:

- `OutletId`
- `OrderNumber`
- `InvoiceSequenceId`
- `InvoiceNumber`
- `InvoiceIssuedAt`

Do not recreate the Orders table.

Recommended behavior:

**When an order is created:**

- Identify its authorized Outlet.
- Generate an internal OrderNumber.
- Save OrderNumber and OutletId.
- Keep InvoiceNumber NULL until invoice issuance.

**When payment successfully completes and an invoice is issued:**

- Verify the order and payment state.
- Retrieve the configured active invoice series.
- Atomically generate the next invoice serial.
- Associate the InvoiceSequenceId and InvoiceNumber with the order.
- Record InvoiceIssuedAt.
- Commit the related database changes consistently.

If payment already succeeded before invoice issuance fails, do not charge or record the payment again. Handle the situation through a controlled recovery workflow.

An issued invoice must preserve its number even if the order is subsequently voided, refunded, or reprinted.

## 4. Concurrency and Transaction Safety

This feature must be safe when multiple cashiers process orders simultaneously.

Use PostgreSQL-compatible mechanisms such as:

- Database transactions.
- `SELECT ... FOR UPDATE`, or atomic `UPDATE ... RETURNING`.
- Unique constraints and indexes.
- Safe counter initialization.
- Transaction retries only when appropriate.
- Idempotency for payment and invoice issuance.

Do not use in-memory counters, static variables, timestamps, or MAX + 1 to generate invoice numbers.

For the default implementation, allocate the invoice number within the same database transaction as invoice issuance where practical, so a failed transaction rolls back the counter update.

Handle concurrent requests without duplicate numbers. Avoid consuming invoice numbers merely for previews.

## 5. Outlet Management Configuration

Extend the existing Outlet Management module.

Do not create an entirely new Outlet Management page.

Add a section for **Invoice Sequence Configuration**.

Display:

- Outlet Name
- Device Identifier
- PTU Number
- MIN Number
- Serial Number
- Active Invoice Series
- Starting Number
- Ending Number
- Last Issued Number
- Next Invoice Number (preview only)
- Sequence Status

Allow an authorized administrator to:

- Configure a new invoice sequence.
- Set the authorized starting and ending numbers.
- Set invoice prefix and padding.
- View the current last-issued number.
- View invoice sequence history.
- Activate or retire a sequence with appropriate validation.

Do not allow normal cashiers to edit invoice sequence settings.

Do not allow resetting LastIssuedNumber on a series that has already issued invoices.

## 6. Recommended API Endpoints

Follow existing API naming conventions where possible.

Suggested endpoints:

**Order Sequence**
- `GET /api/outlets/{outletId}/order-sequence`
- `GET /api/outlets/{outletId}/order-sequence/next-preview`

**Invoice Sequence**
- `GET /api/outlets/{outletId}/invoice-sequences`
- `POST /api/outlets/{outletId}/invoice-sequences`
- `GET /api/outlets/{outletId}/invoice-sequences/active`
- `POST /api/outlets/{outletId}/invoice-sequences/{sequenceId}/activate`

Invoice issuance must be performed internally through an authenticated backend service as part of the approved order/payment workflow.

Do not expose a public endpoint that increments invoice numbers without validating an eligible order.

Preview endpoints must never reserve or consume numbers, and displayed next numbers must not be guaranteed until issuance.

## 7. Receipt / Invoice Integration

Update the existing receipt generation logic to retrieve the persisted InvoiceNumber.

The printed invoice should use the original assigned serial and relevant Outlet registration information.

For a reprint:

- Reuse the existing InvoiceNumber.
- Do not increment the sequence.
- Mark the document as REPRINT.
- Preserve the original issuance information.

Keep the receipt/invoice formatting separate from numbering logic.

## 8. PostgreSQL and EF Core Implementation

Use the existing DbContext and architecture.

Implement:

- EF Core entities and relationships.
- Fluent API configurations.
- PostgreSQL database migrations.
- Required indexes and unique constraints.
- Sequence generation services.
- DTOs and validation.
- Controllers where necessary.
- Dependency injection registration.
- Authorization checks.

Follow the existing PostgreSQL naming conventions and avoid SQL Server-specific syntax such as IDENTITY(1,1), UPDLOCK, HOLDLOCK, and T-SQL.

Ensure existing Orders and Outlets data are migrated safely.

Do not automatically assign invented invoice numbers to historical paid orders. Identify and report records requiring manual review.

## 9. Required Test Cases

Implement or describe tests covering:

1. POS 01 creates its first order and receives POS01-000001.
2. POS 01 creates another order and receives POS01-000002.
3. POS 02 creates its first order with its own independent counter.
4. A paid order receives the correct invoice number.
5. An unpaid order does not receive an invoice number.
6. Concurrent invoice issuance does not create duplicate numbers.
7. Payment retry does not generate a second invoice.
8. Receipt reprint preserves the original invoice number.
9. An invoice sequence cannot issue beyond its configured ending number.
10. An unauthorized cashier cannot modify invoice sequences.
11. A voided issued invoice retains its original serial and audit trail.
12. Replacing or disabling an Outlet does not reset historical numbering.

## 10. Implementation Instructions

Before writing code:

1. Inspect the existing solution structure.
2. Examine Outlet, Orders, Payments, and Receipt implementations.
3. Check whether order numbering or invoice numbering already exists.
4. Identify existing services that should be extended.
5. Summarize the proposed database changes and integration points.

Then implement the feature incrementally.

**Important constraints:**

- Do not recreate existing tables unnecessarily.
- Do not break the existing Payment flow.
- Do not introduce a new POS Terminal entity.
- Reuse OutletId as the POS terminal reference.
- Do not implement inventory or raw-material tracking.
- Do not rewrite unrelated code.
- Preserve the current service and repository patterns.
- Follow existing naming conventions.
- Use asynchronous EF Core operations.
- Keep number generation exclusively on the backend.

After implementation, provide a summary of modified files, database migrations, required configuration, assumptions, and instructions for testing the feature.

Focus on reliable internal order numbering and configurable invoice series management as the foundation for future Philippine BIR-compliant POS registration.
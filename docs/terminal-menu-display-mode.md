# Per-terminal menu display mode deployment

## Contract and scope

`POS.Shared.Models.MenuDisplayMode`: `TextOnly = 0`, `ImageAndText = 1`.
The setting belongs **only to Terminals**, not Branches, Outlets, categories or products.
Omitted fields in old JSON deserialize to TextOnly. Terminal CRUD DTO validation and
explicit server validation reject undefined modes. Existing terminal CRUD routes and
supervisor role restrictions are unchanged. Managers remain branch-scoped; offline
reference now also rejects a supervisor's mismatching branch claim. Cashiers may
fetch only their scoped terminal's offline configuration. Resolve remains authenticated.

## Existing PostgreSQL schema inspected

Actual configured PostgreSQL schema was queried using `BEGIN READ ONLY` plus
`default_transaction_read_only=on`; `transaction_read_only` returned `on`.
Only catalog columns/constraints were queried; no application rows or credentials
were logged, and no DDL/migration was applied to the application database.
`public."Terminals"` currently has:

| Column | PostgreSQL type | Nullable | Default |
|---|---|---|---|
| TerminalId | integer | no | — |
| BranchId | integer | no | — |
| Name | character varying | no | — |
| DeviceIdentifier | character varying | yes | — |
| IsActive | boolean | no | true |
| PTUNumber | character varying | yes | — |
| MINNumber | character varying | yes | — |
| SerialNumber | character varying | yes | — |
| OutletId | integer | no | — |
| TerminalCode | character varying | no | — |
| IpAddress | character varying | yes | — |
| DeactivatedAt | timestamp without time zone | yes | — |
| CreatedAt | timestamp without time zone | no | CURRENT_TIMESTAMP |
| UpdatedAt | timestamp without time zone | no | CURRENT_TIMESTAMP |

`MenuDisplayMode` was absent at discovery. Existing PK is `Terminals_pkey`;
branch FK and `(OutletId, BranchId)` composite outlet ownership FK remain unchanged.
Existing checks cover terminal code and IP consistency. New column is additive:
`integer NOT NULL DEFAULT 0` with `CK_Terminals_MenuDisplayMode` restricting 0/1.
Existing rows therefore retain text-only presentation without data rewrites to fiscal fields.

## Backup and deployment (operator/DBA only)

1. Stop API/administrative writers and coordinate offline terminals. Do not start API
   as a migration mechanism; keep legacy startup migration execution disabled.
2. Record current migration history and capture a full custom-format backup with
   `pg_dump --format=custom --file=<protected-backup> --dbname=<approved-connection>`.
   Supply credentials through approved secret handling, not command-line passwords.
   Verify a restore into a separately named isolated database before proceeding.
3. Review the target database/schema/search_path and permissions explicitly.
   Review `POS.Api/Migrations/TerminalMenuDisplayMode.sql`. It is idempotent and
   **defaults to ROLLBACK**. It refuses incompatible types/null/undefined existing
   values instead of rewriting operator data. Never change `pos_db` during development.
4. For manual deployment, run the script with `psql -X -v ON_ERROR_STOP=1 -f <script>`
   first in a restored isolated database. For approved deployment only, use a reviewed
   copy with its final `ROLLBACK` changed to `COMMIT`. Lock/statement timeouts cause
   a safe abort; investigate blockers rather than removing safety gates blindly.
5. Alternatively generate the incremental idempotent EF script (does not connect):

   ```text
   dotnet ef migrations script 20261026150000_ShiftClosingReconciliation 20261102130945_TerminalMenuDisplayMode --idempotent --project POS.Api/POS.Api.csproj --output <reviewed-output.sql>
   ```

   This requires the prior migrations and `__EFMigrationsHistory` baseline to exist.
   The design-time factory avoids API startup. Do **not** run `database update` as
   part of development. Manual SQL intentionally does not write migration history;
   subsequently executing the approved incremental EF script adopts the guarded
   manual-first schema and records the migration. Do not falsify history by hand.
6. Verify column type/null/default, named constraint, zero undefined rows and unchanged
   terminal IDs, hierarchy, PTU/MIN/serial/IP, invoice counters, orders and payments.
   Deploy API before new UI, then refresh each terminal's reference cache. Mode changes
   affect a terminal when its configuration is refreshed; a sibling must not change.

## Rollback

Prefer rolling application binaries back while **retaining the additive column and
constraint**. Old clients ignore the new JSON field; do not reset invoice counters,
replace terminal identities, or drop the column to roll back a UI preference.
EF `Down` deliberately throws rather than discarding terminal settings. If schema
removal is mandatory, stop all writers/devices and have a DBA restore the verified
backup with a reconciliation plan for any post-backup transactions. A restore may
lose later fiscal activity; it is not an automatic rollback command.

## Web/client behavior and changed files

- `POS.Web/Components/Pages/ManageTerminals.razor`: Add/Edit modal has POS Display Settings. Editing and activate/deactivate preserve the selected mode.
- `POS.Shared/Models/{MenuDisplayMode,TerminalDto,TerminalConfigDto,OfflineReferenceDataDtos}.cs`: stable enum and terminal contracts.
- `POS.Api/Models/{Terminal,HierarchyMapping,PosDbContext.Hierarchy}.cs`, `Controllers/{TerminalsController,OfflineReferenceDataController}.cs`: persistence, mappings and server-side validation. Migration `20261102130945_TerminalMenuDisplayMode` and snapshot carry the additive schema.
- `POS.Client/Components/Pages/NewSale.razor` uses one `Components/Shared/ProductMenuCard.razor` for both category views; its scoped CSS retains the existing text-card rules. Cart selection still calls the existing `AddToCart(product)` handler.
- Image mode uses a fixed 3:2 media frame, `object-fit: contain`, native lazy loading/async decoding, category-color accents, and a local icon placeholder for missing/failed images. Text mode creates no image element or image request.
- `POS.Client/Data/{LocalPosModels,LocalHierarchyUpgrade}.cs`, `Services/{ReferenceDataSyncService,PosSessionService}.cs`, `Components/Pages/Login.razor` and `Components/Layout/MainLayout.razor`: safe SQLite upgrade and normal login/cache propagation. No extra API call or per-card database read is added. Failed refreshes retain the committed mode and local printer assignments.
- Changes become visible after the next online sign-in/reference refresh. No live configuration push or cashier-side override was added.
- Photo bytes use the WebView's normal HTTP cache; the app does not predownload photos. Offline mode retains the display setting, but uncached/unavailable photos show placeholders. Product selection does not await image loading.
- Tests: `POS.Tests/TerminalMenuDisplayModeTests.cs`, `POS.Client.Tests/{MenuDisplayModeCacheTests,ProductMenuCardTests}.cs`, and `POS.Tests/verify_menu_display_{ui,browser,postgres}` scripts. The browser script requires local Chrome and the rendered fixture produced by the client harness.

## Verification

- `dotnet run --project POS.Tests/POS.Tests.csproj --artifacts-path <scratch-output>`:
  68 passed, 0 failed. MENU01–05 cover legacy/numeric contracts and validation,
  persistence/CRUD/list/resolve/offline mappings, sibling isolation, undefined values,
  supervisor attributes plus branch tests, Npgsql metadata and safe migration shape.
- Strict executable RED→GREEN cycles caught missing enum, dropped mapping, invalid
  server values, offline cross-branch access and absent database constraint/safe DDL.
- `python POS.Tests/verify_menu_display_postgres.py <incremental-ef.sql>` creates a
  **new private UUID PostgreSQL cluster** and test database, never uses application
  configuration or `pos_db`, and stops/deletes that cluster afterward. Tests exercise
  legacy and new-row defaults, constraints/nulls, repeat/manual-first EF application,
  dry-run rollback, sibling independence and preservation of identity/fiscal fixtures.
- `dotnet run --project POS.Client.Tests/POS.Client.Tests.csproj --artifacts-path <scratch-output> -p:NoWarn=BL0006`: 17 passed, 0 failed, including seven SQLite/cache groups and actual Razor render/event dispatch checks. The optional unrelated opening-float PostgreSQL group skips without a disposable connection.
- `python POS.Tests/verify_menu_display_ui.py`: 3 passed. `node POS.Tests/verify_menu_display_browser.cjs`: actual Razor output plus project CSS passed 1280/768/375px checks for no horizontal overflow, touch size, category color, focus, image sizing and placeholders. Text mode made zero photo requests; image fixture made one. This is an isolated browser fixture, not a hosted MAUI/Web sign-in session.
- API, Shared, Web and Client Windows/Android builds succeeded with zero errors. Existing nullable/platform warnings and dependency advisories remain; no dependency upgrade was included.
- Existing receipt checks still passed: `verify_thermal_receipt_layout.py` 7/7 and `verify_receipt_printer.py` 6/6. `git diff --check` passed. Graph update could not run because `graphify` is not installed; no graph artifact existed at initial discovery.
- Authorization is verified by reflected MVC authorization metadata and actual controller
  branch/terminal decisions, **not** a hosted HTTP pipeline. No API was started.
- Existing nullable/obsolete warnings and SQLite package NU1903 advisory remain outside
  this change. Installed scratch EF tool 10.0.0 warns runtime is 10.0.13; generation,
  migration discovery and isolated PostgreSQL execution are verified separately.

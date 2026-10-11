# Business types database change

Apply `BusinessTypes.sql` manually against PostgreSQL `pos_db` with `search_path` set to the application's schema (`public`). It is **not** run on application startup. Review/back up the database before applying in another environment.

The script is transactional and re-runnable:
- Creates `"BusinessType"` with lowercase `id`, `type` (50 characters), and `description` (250 characters).
- Enforces required, nonblank values, normalized uppercase type, and a unique type index.
- Seeds RETAIL, FNB, SERVICE, HYBRID only when absent; does not overwrite edited descriptions.
- Adds nullable `"Branches"."BusinessTypeId"`, an index, and an `ON DELETE RESTRICT` foreign key. Existing branches are **not assigned** a type.
- No existing branch/user/transaction data is deleted or rewritten.

The supplied API and Web changes require this schema change before use. Removing the schema change requires removing the branch FK/column before dropping the table and loses business-type assignments; no automatic destructive rollback is supplied.

## Verification

```bash
POS_TEST_POSTGRES=1 dotnet test POS.Tests/POS.Tests.csproj --filter FullyQualifiedName~BusinessType --nologo -v:q -p:OutputPath=bin/BusinessTypes/
```

The PostgreSQL test is opt-in. It reads the existing context's connection internally, refuses any host other than localhost/127.0.0.1/::1 or database other than pos_db, creates a unique temporary schema, runs the script twice, verifies seeds/unique/FK/restrict behavior, and removes that schema in finally. It does not edit application records. Other feature tests use an isolated InMemory API; the browser test runs the actual Web page and Shared HTTP adapter against that API.

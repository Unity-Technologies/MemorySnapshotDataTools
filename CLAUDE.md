# Project guidance for Claude

MemorySnapshotDataTool parses Unity memory snapshot (`.snap`) files and exports them to
DuckDB / SQLite databases, then runs SQL to build HTML reports. Because the whole tool is
built around composing and executing SQL, **SQL safety is a first-class rule in this repo.**

## Rule: never build SQL from unsanitized external data

External / untrusted data includes anything not hard-coded in source: CLI arguments,
file names and paths, environment values, fields read from a `.snap` file, values read
back out of the database, and anything derived from them. **Never** concatenate or string-
interpolate such data into a SQL statement.

When you write or modify any code that builds a SQL string, follow these in order:

1. **Bind values as parameters — always the default for any *value*.**
   - **SQLite** (`Microsoft.Data.Sqlite`) — named parameters with a `$` prefix:
     ```csharp
     cmd.CommandText = "SELECT 1 FROM pragma_table_info($t) WHERE name = $c LIMIT 1";
     cmd.Parameters.AddWithValue("$t", tableName);
     cmd.Parameters.AddWithValue("$c", columnName);
     ```
   - **DuckDB** (`DuckDB.NET`) — positional `?` parameters in order:
     ```csharp
     cmd.CommandText = "... WHERE table_name = ? AND column_name = ? LIMIT 1";
     cmd.Parameters.Add(new DuckDBParameter { Value = tableName });
     cmd.Parameters.Add(new DuckDBParameter { Value = columnName });
     ```

2. **Identifiers (table / column names) can't be parameters in most positions.**
   Validate them against a hard-coded safe-list, or query a catalog table that *does*
   accept parameters (`information_schema.columns` for DuckDB, `pragma_table_info($t)`
   for SQLite) instead of splicing the name into the statement.

3. **Only interpolate a value directly if it is a non-string numeric type** (`int`,
   `long`, …) that you control — a strongly-typed number cannot carry a payload. Add a
   comment saying why it is safe, and prefer a parameter anyway when the API allows one.

4. **Never** do `"... WHERE x = '" + value + "'"` or `$"... '{value}'"` with a string value.

5. **Open read-only when a path only reads.** Report/analysis code that never writes opens the
   database with least privilege — `Data Source=<path>;Mode=ReadOnly` (SQLite) or
   `Data Source=<path>;ACCESS_MODE=READ_ONLY` (DuckDB) — so a bad query can't mutate data. Only
   the export writers open read-write.

## Canonical safe examples already in this repo

Match these when you touch SQL — don't reinvent the pattern:

- `Core/Report/Queries/SqliteReportQueries.cs` `HasColumn` — parameterized `pragma_table_info`.
- `Core/Report/Queries/DuckDbReportQueries.cs` `HasColumn` — identifier check via catalog table.
- `Core/Report/MultiSnapshotReport/MultiSnapshotReportBuilder.cs` `HasColumn` — both dialects parameterized.
- `Core/ExportDestination/DuckDbExportDestination.cs` — positional `?` parameters for inserts.
- `Core/ExportDestination/SqliteWriter.cs` — bulk inserts with `$pN` parameters.

## The `ExecuteQuery(string sql)` contract

`IReportQueryBackend.ExecuteQuery(string sql)` runs a raw SQL string and has **no parameter
overload**. It must therefore only ever receive an internally-constructed query — a constant
from `ReportSql` or one of its builders — never external input. The single dynamic builder,
`ReportSql.DownstreamStats(long rootIdx)`, interpolates a numeric `long`, which is injection-
safe. If you ever need to pass a *value* into a report query, add a parameterized path rather
than interpolating it into the SQL string. As defense-in-depth, the backends behind this sink
open the database read-only (rule 5 above), so a malformed query cannot mutate data.

## Best-practices reference

Full guidance for both humans and Claude — with rationale, anti-patterns, and review
checklist — is in [`docs/sql-safety.md`](docs/sql-safety.md). Read it before adding any new
query path or query builder.

## Build & test

- Build: `dotnet build MemorySnapshotDataTools.sln`
- Test: `dotnet test MemorySnapshotDataTools.sln`

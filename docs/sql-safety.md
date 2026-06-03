# SQL safety: avoiding SQL injection

This tool is built around composing and executing SQL against DuckDB and SQLite databases.
This page is the canonical reference for writing query code safely. It is written for both
human contributors and for Claude (the project rule lives in
[`CLAUDE.md`](https://github.com/Unity-Technologies/MemorySnapshotDataTools/blob/main/CLAUDE.md)
and points here).

## The one rule

**Never build a SQL statement by concatenating or interpolating external data.** Bind data
as parameters instead.

"External data" is anything not hard-coded in source:

- CLI arguments (directory, filter, title, output paths, …)
- File names and filesystem paths
- Environment variables
- Fields parsed out of a `.snap` snapshot file (type names, object names, area names, …)
- Values read back out of the database
- Anything computed or derived from any of the above

Even when a value happens to be a compile-time constant today, prefer the safe pattern so
the code stays safe when someone later swaps the constant for a variable.

## Why it matters

When external text is spliced straight into SQL, a value such as
`'; DROP TABLE native_objects;--` is parsed as *code* rather than *data*. Parameter binding
keeps the SQL logic and the data on separate channels, so the database engine always treats
bound values as literals — never as SQL. This is the prevention technique recommended by the
[OWASP SQL Injection Prevention Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/SQL_Injection_Prevention_Cheat_Sheet.html).

## Patterns by category

### 1. Values → always parameterize

This is the default for every value (numbers, strings, blobs, dates). The two database
libraries used in this repo bind parameters differently.

**SQLite — `Microsoft.Data.Sqlite`** uses **named** parameters with a `$` prefix:

```csharp
using var cmd = connection.CreateCommand();
cmd.CommandText = "SELECT COUNT(*) FROM native_objects WHERE native_type_name = $type";
cmd.Parameters.AddWithValue("$type", typeName);
```

**DuckDB — `DuckDB.NET`** uses **positional** `?` parameters, bound in order:

```csharp
using var cmd = connection.CreateCommand();
cmd.CommandText = "SELECT COUNT(*) FROM native_objects WHERE native_type_name = ?";
cmd.Parameters.Add(new DuckDBParameter { Value = typeName });
```

For bulk inserts, generate numbered placeholders and bind each value — see
`SqliteWriter.CreateBulkInsertCommand` (`$p0`, `$p1`, …) and the `native_objects`/
`managed_objects` writers, and the positional inserts in `DuckDbExportDestination`.

### 2. Identifiers (table / column names) → safe-list or catalog lookup

A parameter placeholder cannot stand in for a table or column name in most SQL positions,
so identifiers need a different defense:

- **Safe-list:** compare against a hard-coded set of known-good identifiers and reject
  anything else. In this repo, table and column names are themselves constants, which is the
  simplest form of a safe-list.
- **Catalog lookup with parameters:** to check whether a column exists, query a catalog
  table/function that *does* accept a bound parameter rather than splicing the name in.

```csharp
// SQLite — pragma_table_info accepts a bound parameter:
sqliteCmd.CommandText = "SELECT 1 FROM pragma_table_info($t) WHERE name = $c LIMIT 1";
sqliteCmd.Parameters.AddWithValue("$t", tableName);
sqliteCmd.Parameters.AddWithValue("$c", columnName);

// DuckDB — information_schema.columns is a regular table, so it accepts parameters:
cmd.CommandText =
    "SELECT 1 FROM information_schema.columns " +
    "WHERE table_schema = 'main' AND table_name = ? AND column_name = ? LIMIT 1";
cmd.Parameters.Add(new DuckDBParameter { Value = tableName });
cmd.Parameters.Add(new DuckDBParameter { Value = columnName });
```

If you ever must place an identifier literally and parameters genuinely can't reach the
position, validate it against a safe-list first; only as a last resort escape it (e.g.
doubling single quotes for a quoted string argument), and document why.

### 3. Numeric values you control → direct interpolation is acceptable

A strongly-typed number cannot carry an injection payload, so interpolating a `long`/`int`
that you produced (for example, an index read from your own query result) is acceptable when
no parameter API is available. Keep the variable strongly typed — never widen it to `string`
— and leave a comment explaining the guarantee.

```csharp
// rootIdx is a long sourced from our own query result; a numeric literal can't inject.
var sql = $"... WHERE c.from_index = {rootIdx} ...";
```

This is exactly how `ReportSql.DownstreamStats(long rootIdx)` works.

### 4. SQL fragments assembled from a fixed set → not data, keep them constant

Some queries are assembled from alternative SQL *expressions* chosen at runtime (e.g. a
resident-size expression that differs by dialect, or a `COALESCE`→`IFNULL` rewrite for
SQLite). These fragments are hard-coded source, not external data, so composing them is fine
— but the inputs that decide *which* fragment to use must come from a closed set you control,
never from external text.

## The `ExecuteQuery(string sql)` contract

`IReportQueryBackend.ExecuteQuery(string sql)` executes a raw SQL string and has **no
parameter overload**. By contract it must receive only internally-constructed SQL — a
constant from `ReportSql` or one of its builders — and **never** external input. The only
dynamic builder, `ReportSql.DownstreamStats`, interpolates a numeric `long` (safe per
category 3).

If a future report query needs a real *value* from outside, do **not** interpolate it into
the string passed to `ExecuteQuery`. Add a parameterized execution path instead.

## Least privilege: open read paths read-only

`ExecuteQuery(string sql)` runs a whole query string, so it cannot be parameterized away — the
defense for that sink is least privilege. Code paths that only read (the report query backends,
the multi-snapshot builder, the summary runner) open the database **read-only**, so even a
malformed or unexpected query cannot modify or drop data:

- **SQLite:** `Data Source=<path>;Mode=ReadOnly`
- **DuckDB:** `Data Source=<path>;ACCESS_MODE=READ_ONLY`

Only the export writers (`DuckDbExportDestination`, `SqliteWriter`), which create the database,
open read-write. When you add a new code path that only reads from a database, open it read-only.

## Anti-patterns — never do these

```csharp
// ❌ String value concatenated into SQL
var sql = "SELECT * FROM native_objects WHERE name = '" + name + "'";

// ❌ String value interpolated into SQL
var sql = $"SELECT * FROM native_objects WHERE name = '{name}'";

// ❌ Identifier interpolated with no safe-list / escaping
var sql = $"SELECT 1 FROM pragma_table_info('{tableName}')";

// ❌ Passing externally-influenced text to ExecuteQuery(string sql)
backend.ExecuteQuery("SELECT * FROM " + userSuppliedTable);
```

## Review checklist

Before merging code that touches SQL, confirm:

- [ ] Every **value** in the statement is a bound parameter (`$name` for SQLite,
      `?` for DuckDB) — not concatenated or interpolated.
- [ ] Every **identifier** is a hard-coded constant, validated against a safe-list, or
      resolved through a parameterized catalog query.
- [ ] Any directly-interpolated value is a strongly-typed number you control, with a comment
      explaining why it's safe.
- [ ] Nothing passed to `ExecuteQuery(string sql)` is, or is derived from, external input.
- [ ] New code matches an existing safe example rather than introducing a new pattern.

## Canonical safe examples in this codebase

- `Core/Report/Queries/SqliteReportQueries.cs` — parameterized `pragma_table_info`.
- `Core/Report/Queries/DuckDbReportQueries.cs` — identifier existence check via catalog table.
- `Core/Report/MultiSnapshotReport/MultiSnapshotReportBuilder.cs` — `HasColumn` parameterized for both dialects.
- `Core/ExportDestination/DuckDbExportDestination.cs` — positional `?` parameter inserts.
- `Core/ExportDestination/SqliteWriter.cs` — bulk inserts with `$pN` parameters.

## References

- [OWASP — SQL Injection](https://owasp.org/www-community/attacks/SQL_Injection)
- [OWASP — SQL Injection Prevention Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/SQL_Injection_Prevention_Cheat_Sheet.html)

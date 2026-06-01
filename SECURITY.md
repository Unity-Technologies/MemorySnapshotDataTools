# Security

## Reporting a vulnerability

Report suspected security issues to **zack.asofsky@unity3d.com**. Please do not open public issues for undisclosed vulnerabilities.

## SQL safety

This tool composes and executes SQL against DuckDB and SQLite. Safe-query rules and best
practices are documented in [`docs/sql-safety.md`](docs/sql-safety.md), with the enforced rule
for contributors (and Claude) in [`CLAUDE.md`](CLAUDE.md). In short: bind values as parameters,
never interpolate external data, validate identifiers against a safe-list or parameterized
catalog lookup, and open read-only on any path that only reads.

## SAST finding triage log

Findings from static analysis (Cycode SAST) that have been reviewed and accepted are recorded
here so the rationale is durable and reviewable. Suppression itself is performed in Cycode, not
in this repo — see "How we suppress" below.

### CYCODE-SAST: Unsanitized external input in SQL query — `ExecuteQuery`

| Field | Value |
| --- | --- |
| Scanner | Cycode SAST |
| Rule | Unsanitized external input in SQL query (SQL injection) |
| Location | [`Core/Report/Queries/SqliteReportQueries.cs`](Core/Report/Queries/SqliteReportQueries.cs) — `ExecuteQuery(string sql)` (and the equivalent `DuckDbReportQueries.ExecuteQuery`) |
| Reviewed | 2026-06-01, Zack Asofsky |
| Disposition | **False positive** — no external/untrusted input reaches the sink — accepted with defense-in-depth mitigations |

**Rationale.** `ExecuteQuery(string sql)` only ever receives internally-constructed queries:
compile-time constants from `ReportSql` and its builders. The sole dynamic value,
`ReportSql.DownstreamStats(long rootIdx)`, interpolates a numeric `long`, which cannot carry an
injection payload. No CLI argument, file path, or snapshot field reaches the query string.

**Defense-in-depth mitigations applied** (branch `bugfix/sql-sanitization`):

- Report/analysis database connections are opened **read-only** (`Mode=ReadOnly` for SQLite,
  `ACCESS_MODE=READ_ONLY` for DuckDB), so a malformed query reaching this sink cannot modify or
  drop data. Only the export writers open read-write.
- Identifier-bearing helper queries (`HasColumn`) use parameterized catalog lookups
  (`pragma_table_info($t)` / `information_schema.columns` with bind parameters) instead of
  string concatenation.
- The multi-snapshot native-type filter binds its value as a parameter rather than interpolating
  it.
- The `ExecuteQuery` contract is documented on `IReportQueryBackend`, and the rules are codified
  in [`docs/sql-safety.md`](docs/sql-safety.md) and [`CLAUDE.md`](CLAUDE.md).
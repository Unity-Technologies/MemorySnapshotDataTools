---
name: memory-db-sql
description: Write and manage SQL queries against an exported Unity memory-snapshot database (DuckDB/SQLite), and keep the schema, views, version, and docs consistent. Use when writing/editing queries over native_objects, native_allocations, native_roots, memory_regions, system_memory_regions, etc.; investigating memory regions; adding or changing tables/views/macros; or checking database version compatibility.
---

# Memory snapshot database SQL

Use this when working with the database the tool exports from a `.snap` (the `export` command), as
opposed to running the export/report pipeline itself (use `memory-snapshot-report` for that).

**Canonical schema reference:** [`docs/database-schema.md`](../../../docs/database-schema.md). It
lists every table, column, view, macro, join key, and the version policy. Read it before writing
non-trivial queries — this skill is the workflow; that doc is the source of truth.

## Before you query: check the schema version

The database stamps a **major.minor** version in `schema_meta`:

```sql
SELECT schema_version_major, schema_version_minor, msdt_version FROM schema_meta;
```

- **No `schema_meta` table** → a pre-versioning export (treated as 0.0): re-export the `.snap`.
- **major < `DatabaseSchemaInfo.SchemaMajor`** → table/column structure changed; **re-export required**
  (`MemorySnapshotDataTools export "<snap>" "<db>"`).
- **same major, minor < `DatabaseSchemaInfo.SchemaMinor`** → only views/indexes changed; **upgrade in
  place** with `MemorySnapshotDataTools upgrade "<db>"` (no re-export).
- In code, classify with `DatabaseSchemaInfo.Evaluate(major, minor)` → `SchemaAction`
  (`None`/`UpgradeInPlace`/`ReExport`/`ToolOutdated`); the CLI `SchemaGate` already does this before
  `report`/`summary`/`validate`, and `DatabaseMaintenance.{Inspect,UpgradeInPlace}` are the entry
  points (see [`Core/Models/DatabaseSchemaInfo.cs`](../../../Core/Models/DatabaseSchemaInfo.cs)).

## Querying: the things that bite

These are spelled out in the schema doc, but the high-frequency traps:

- **Two region tables, no FK between them.** `memory_regions` = Unity allocator buckets
  (`native_allocations.memory_region_index` points here). `system_memory_regions` = OS/VM regions
  (the RAM truth). Bridge an allocation to its OS region **by address range**, not a key.
- **Prefer the views/macros** over rebuilding joins: `v_allocation_enriched` (allocation + Unity
  region + OS region + root + object), `v_system_region_summary` (committed/resident/Unity-tracked
  per OS region), `v_region_owner_breakdown`, `v_connection_edges` (reference graph with both
  endpoints typed — filter it, don't `SELECT *`), `v_assetbundle_utilization` (per-bundle: does it
  reference other loaded assets, and how much). DuckDB also has `region_allocations(name)` and
  `region_page_density(name)` (SQLite has the views only).
- **Connections: don't hand-count "own" edges with magic numbers.** A native object's outbound edges
  include its self-reference and its managed wrappers (`native_gc_handle_bridge`/`native_connection`
  to managed). For "references to *other* loaded objects," count `native_object→native_object`
  `native_connection` edges where `to_index <> from_index` — exactly what `v_assetbundle_utilization`
  does.
- **`native_object_address` ≠ `native_allocations.address`** — bridge objects and allocations
  through a shared `root_reference_id` → `native_roots.root_id`, never by address.
- **Don't divide by `memory_regions.address_size`** (0 for `ALLOC_DEFAULT` et al.).
- **`system_memory_regions.type` is uniformly 0 on iOS** — group by `name`.
- **Resident data and `page_size` require `snap_format_version` ≥ 17** (else NULL).
- `region_page_density` is for **small-allocation zones** (NANO/TINY/SMALL); `avg_fill_pct` > 100%
  means the region holds page-spanning allocations and the metric doesn't apply.

## Writing query code safely

This repo's first-class rule is SQL safety — see [`CLAUDE.md`](../../../CLAUDE.md) and
[`docs/sql-safety.md`](../../../docs/sql-safety.md):

- **Parameterize every value.** DuckDB: positional `?`. SQLite: named `$name`.
- **Identifiers can't be parameters** — validate against the catalog (`information_schema.columns`
  for DuckDB, `pragma_table_info($t)` for SQLite); see the `HasColumn` helpers.
- **Open read-only** for analysis (`ACCESS_MODE=READ_ONLY` / `Mode=ReadOnly`).
- The report `ExecuteQuery(string)` sink takes only internally-constructed SQL (constants in
  `ReportSql`); never pass it external input.

Ad-hoc CLI querying (no SQL command exists in the tool): use the `duckdb` CLI on a `.duckdb` file
(open with `-readonly`; if a lock error mentions DataGrip, ask the user to close that connection).

## Upkeep: when you change the schema

A schema change is not done until **all** of these are consistent:

1. **Both backends** — mirror the change in
   [`DuckDbExportDestination.cs`](../../../Core/ExportDestination/DuckDbExportDestination.cs) **and**
   [`SqliteWriter.cs`](../../../Core/ExportDestination/SqliteWriter.cs) (tables, indexes, and the
   `CreateViewsScript`). Remember: DuckDB has `ASOF` joins and table macros; SQLite does not (use a
   correlated subquery; macros are DuckDB-only). Keep index/view DDL **re-runnable** (`CREATE INDEX
   IF NOT EXISTS`, DuckDB `CREATE OR REPLACE VIEW` / SQLite drop-then-create) so `UpgradeSchema` works.
2. **The doc** — update [`docs/database-schema.md`](../../../docs/database-schema.md): table/column
   tables, view/macro list, join keys, and the version table. New tables, views, columns, and
   identifiers must appear here.
3. **The version** — in
   [`Core/Models/DatabaseSchemaInfo.cs`](../../../Core/Models/DatabaseSchemaInfo.cs):
   - **View/index change only** (add/change a view or index): bump **`SchemaMinor`**. Existing
     databases can be upgraded in place (`MemorySnapshotDataTools upgrade`), so no re-export.
   - **Table/column change** (add/rename/remove a table or column, or change a column's
     meaning/units): bump **`SchemaMajor`** and reset `SchemaMinor` to 0. Old databases require a
     re-export. Make sure both writers' table DDL and `schema_meta` insert stay in sync.
   - Add a row to the doc's version table either way.
4. **Readers** — `snapshot_info` is read with `SELECT *` by column name
   (`SummaryReportRunner.ReadSnapshotInfo`), so new columns are safe; if you add a column some
   reader needs, wire it there.
5. **Tests** — extend `DatabaseSchemaInfoTests` and the export round-trip tests so the new
   schema/view/version is asserted.

## See also

- `memory-snapshot-report` skill — export a `.snap` to a database and generate reports.
- [`docs/database-schema.md`](../../../docs/database-schema.md) — canonical schema.

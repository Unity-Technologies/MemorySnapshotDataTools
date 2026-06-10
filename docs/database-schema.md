# Exported database schema

This is the **canonical reference** for the database that the tool produces from a Unity
memory snapshot (`.snap`). It covers the schema version, every table and column, the analysis
views and macros, and the join keys you need to query native memory correctly.

The same logical schema is written to both backends:

- **DuckDB** (`.duckdb`, recommended) — created by [`DuckDbExportDestination`](https://github.com/Unity-Technologies/MemorySnapshotDataTools/blob/main/Core/ExportDestination/DuckDbExportDestination.cs).
- **SQLite** (`.db`) — created by [`SqliteWriter`](https://github.com/Unity-Technologies/MemorySnapshotDataTools/blob/main/Core/ExportDestination/SqliteWriter.cs).

> **Keep this doc in sync.** Any change to a table, column, view, or macro must be reflected
> here in the same change, and breaking changes must bump the schema version. See
> [Schema version](#schema-version) and the `memory-db-sql` Claude skill for the checklist.

---

## Schema version

Every exported database records a **two-part version** in the **`schema_meta`** table so tools can
tell whether a database needs a full re-export or just an in-place refresh.

```sql
SELECT schema_version_major, schema_version_minor, msdt_version, created_at_utc FROM schema_meta;
```

The versions are defined once in code by
[`DatabaseSchemaInfo`](https://github.com/Unity-Technologies/MemorySnapshotDataTools/blob/main/Core/Models/DatabaseSchemaInfo.cs)
(`SchemaMajor`, `SchemaMinor`), which both writers stamp and the CLI checks.

| Part | Meaning | Bump when… | A lower value means |
|------|---------|------------|---------------------|
| **major** (`SchemaMajor`) | Table/column **structure** | You add/rename/remove a table or column, or change a column's meaning/units | **Re-export required** — the data itself must be re-extracted from the `.snap` |
| **minor** (`SchemaMinor`) | Derived **views and indexes** | You add/change a view or index | **Upgradeable in place** — re-run the view/index DDL, no re-export |

Reset minor to 0 whenever you bump major.

| Version | Changes |
|---------|---------|
| 1.0 | First versioned schema: `schema_meta`, `snapshot_info.page_size`, region analysis views/macros. |
| 1.1 | Added `v_connection_edges` and `v_assetbundle_utilization` views (minor — upgradeable in place). |
| 1.2 | Reformulated `v_connection_edges` joins (kind check folded into the join key) so DuckDB hash-joins instead of nested-loop — `SELECT … WHERE from_type=…` drops from minutes to sub-second (minor). |
| 1.3 | Added `v_assetbundle_loaded_assets` view — one row per (AssetBundle, loaded native object) it references (minor — upgradeable in place). |

**What the CLI does.** Before a read command (`report`, `summary`, `validate`),
`DatabaseSchemaInfo.Evaluate(major, minor)` classifies the database and the CLI acts:

| Classification | Meaning | CLI behavior |
|----------------|---------|--------------|
| `None` | Current | Proceeds silently. |
| `UpgradeInPlace` | Same major, older minor | Offers to upgrade in place (interactive prompt; non-interactive prints `… upgrade "<db>"`). |
| `ReExport` | Older/pre-versioning major | Prints the exact `export` command; if the source `.snap` still exists at `snapshot_info.snapshot_path`, offers to re-export it now. |
| `ToolOutdated` | DB newer than the tool | Warns to update the tool. |

Run an in-place minor upgrade explicitly with:

```bash
MemorySnapshotDataTools upgrade <database.duckdb|.db>
```

This re-applies indexes and views (`DatabaseMaintenance.UpgradeInPlace`) and bumps the stored minor
version, then lists which schema versions were applied (from `DatabaseSchemaInfo.ChangesSince`, the
same per-version summaries as the [version table](#schema-version) below). It refuses major-version
gaps and tells you to re-export instead. Non-interactive sessions (stdin redirected) never auto-modify
a database — they only print the advisory and command.

```text
Upgraded database schema from v1.2 to v1.3.
Applied (views/indexes re-created):
  • v1.3: Added v_assetbundle_loaded_assets view (the assets each AssetBundle keeps loaded).
```

The stored version is also **displayed in output**: `summary` prints a `Schema` field, the HTML
`report` shows a *Schema Version* row in Snapshot Info, and `multi-report` shows it per database
(as a tooltip, with a ⚠ marker when behind) — each via `DatabaseSchemaInfo.DescribeVersion`.

---

## Tables

Column types shown are DuckDB; SQLite uses `INTEGER`/`TEXT` equivalents (DuckDB `BIGINT` → SQLite
`INTEGER`, `VARCHAR` → `TEXT`, `BOOLEAN` → `INTEGER` 0/1). Sizes are **bytes** unless the column
name says otherwise.

### `schema_meta`
One row. The schema version stamp.

| Column | Type | Notes |
|--------|------|-------|
| `schema_version_major` | INTEGER | `DatabaseSchemaInfo.SchemaMajor` at export time. Lower → re-export. |
| `schema_version_minor` | INTEGER | `DatabaseSchemaInfo.SchemaMinor`. Lower (same major) → upgrade in place. |
| `msdt_version` | VARCHAR | MemorySnapshotDataTools build version. |
| `created_at_utc` | VARCHAR | Export timestamp (ISO-8601 UTC). |

### `snapshot_info`
One row. Provenance of the capture.

| Column | Type | Notes |
|--------|------|-------|
| `snapshot_path` | VARCHAR | Source `.snap` path. |
| `exported_at_utc` | VARCHAR | When the export ran (ISO-8601 UTC). |
| `unity_version` | VARCHAR | Unity version, or `format:<n>` fallback. |
| `snap_format_version` | INTEGER | Snapshot format version (resident data requires ≥ 17). |
| `session_guid` | BIGINT | Profiler session GUID, or NULL. |
| `product_name` | VARCHAR | Project/product name, or NULL. |
| `platform` | VARCHAR | Runtime platform (e.g. `IPhonePlayer`, `OSXPlayer`), or NULL. |
| `record_date_utc` | VARCHAR | Capture timestamp, when known. |
| `page_size` | BIGINT | OS page size of the captured device (e.g. 16384 iOS arm64, 4096 elsewhere); NULL when unknown (format < 17). Used by `region_page_density`. |

### `native_objects`
High-level Unity objects (textures, meshes, GameObjects…). One row per native object.

| Column | Type | Notes |
|--------|------|-------|
| `native_object_index` | INTEGER PK | Zero-based index; target of `connections` with `kind='native_object'`. |
| `instance_id` | VARCHAR | Unity instance id (string). |
| `name` | VARCHAR | Object name. |
| `size_bytes` | BIGINT | Object's own native size. |
| `native_object_address` | BIGINT | Object pointer. **Not** an allocation address — see [gotchas](#gotchas). |
| `root_reference_id` | BIGINT | → `native_roots.root_id` (−1 when unknown). |
| `type_index` | INTEGER | Index into native type names. |
| `native_type_name` | VARCHAR | Resolved type (e.g. `Texture2D`). |
| `is_destroyed` | BOOLEAN | Marked destroyed but still resident. |
| `resident_size_bytes` | BIGINT | Resident bytes for the object's root (format ≥ 17), else NULL. |

### `managed_objects`
Managed (C#) heap objects. One row per managed object.

| Column | Type | Notes |
|--------|------|-------|
| `managed_object_index` | INTEGER PK | Target of `connections` with `kind='managed_object'`. |
| `address` | BIGINT | Managed heap address. |
| `size_bytes` | BIGINT | Size. |
| `type_index` | INTEGER | Index into managed type descriptions. |
| `managed_type_name` | VARCHAR | Resolved managed type. |
| `native_object_index` | BIGINT | → `native_objects.native_object_index`, or NULL (orphaned wrapper). |

### `connections`
Directed edges of the object reference graph.

| Column | Type | Notes |
|--------|------|-------|
| `from_kind` | VARCHAR | `native_object` or `managed_object`. |
| `from_index` | BIGINT | Index into the corresponding object table. |
| `to_kind` | VARCHAR | `native_object` or `managed_object`. |
| `to_index` | BIGINT | Index into the corresponding object table. |
| `connection_type` | VARCHAR | e.g. `native_connection`, `GCHandle`. |

### `native_roots`
Unity memory areas. Backbone for attribution. One row per root; `root_id` is **unique**.

| Column | Type | Notes |
|--------|------|-------|
| `root_index` | INTEGER PK | Zero-based index. |
| `root_id` | BIGINT | Join key for `native_objects.root_reference_id` and `native_allocations.root_reference_id`. |
| `area_name` | VARCHAR | Subsystem grouping (`System`, `Managers`, `Objects`, `SerializedFile`, `Rendering`…). |
| `object_name` | VARCHAR | Root's object name. |
| `accumulated_size_bytes` | BIGINT | Committed bytes attributed to this root. |
| `resident_size_bytes` | BIGINT | Resident bytes (format ≥ 17), else NULL. |

### `memory_regions`
Unity's **internal allocator** buckets (`ALLOC_DEFAULT`, `ALLOC_GFX`, TLSF blocks, temp/stack
allocators). **Not** OS regions — see [the two region tables](#the-two-region-tables).

| Column | Type | Notes |
|--------|------|-------|
| `region_index` | INTEGER PK | Target of `native_allocations.memory_region_index`. |
| `address_base` | BIGINT | Allocator block base. |
| `address_size` | BIGINT | Allocator block reserve. **Often 0** for grouping allocators (e.g. `ALLOC_DEFAULT`) — do not use as a container bound. |
| `name` | VARCHAR | Allocator name. |
| `parent_region_index` | INTEGER | → `memory_regions.region_index` (hierarchy), or NULL. |
| `first_allocation_index` | INTEGER | → `native_allocations.allocation_index`, or NULL. |
| `num_allocations` | INTEGER | Allocation count in this bucket. |

### `native_allocations`
Low-level allocations Unity's allocators requested. One row per allocation.

| Column | Type | Notes |
|--------|------|-------|
| `allocation_index` | INTEGER PK | Zero-based index. |
| `address` | BIGINT | Allocation address. Falls inside a `system_memory_regions` range. |
| `size_bytes` | BIGINT | Payload size (live bytes). |
| `overhead_size_bytes` | BIGINT | Allocator overhead. |
| `padding_size_bytes` | BIGINT | Alignment padding. |
| `memory_region_index` | INTEGER | → `memory_regions.region_index` (Unity allocator), or NULL. |
| `root_reference_id` | BIGINT | → `native_roots.root_id`, or NULL. |

### `system_memory_regions`
OS / virtual-memory regions — what `vmmap` reports (`MALLOC_NANO`, `MALLOC_LARGE`, dyld shared
cache, `IOACCELERATOR`, framework/dylib mappings…). The ground truth for process RAM. **No foreign
keys** — bridge to allocations by address range only.

| Column | Type | Notes |
|--------|------|-------|
| `region_index` | INTEGER PK | Zero-based index. |
| `address` | BIGINT | Region base. |
| `size_bytes` | BIGINT | Committed / virtual size. |
| `resident_bytes` | BIGINT | Physical RAM resident. |
| `type` | INTEGER | Region type code (frequently `0` for all rows on iOS — use `name`). |
| `name` | VARCHAR | Region name. |

### `summary_metrics`
MemoryProfiler "Summary" page breakdown (Allocated Memory Distribution + Managed Heap Utilization).

| Column | Type | Notes |
|--------|------|-------|
| `metric_group` | VARCHAR | Group label. |
| `category` | VARCHAR | Category label. |
| `committed_bytes` | BIGINT | Committed bytes. |
| `resident_bytes` | BIGINT | Resident bytes. |
| `resident_available` | INTEGER | 1 if resident data is available, else 0. |

---

## Views and macros

These remove the repetitive joins needed to analyze native memory. **Views exist on both
backends; macros are DuckDB-only** (SQLite has no table macros — query the view directly, see
[SQLite differences](#sqlite-differences)).

### `v_allocation_enriched` (view)
One row per allocation, joined to its Unity allocator bucket, the **OS region containing its
address**, its root, and its owning object.

Columns: `allocation_index`, `address`, `size_bytes`, `overhead_size_bytes`, `padding_size_bytes`,
`memory_region_index`, `unity_region_name`, `system_region_index`, `system_region_name`,
`root_reference_id`, `area_name`, `root_object_name`, `native_object_index`, `native_type_name`,
`object_name`.

`system_region_*` is NULL for the rare allocation that falls in a gap between OS regions. DuckDB
resolves the containing region with an `ASOF` join; SQLite with an equivalent correlated subquery
(nearest region whose range covers the address).

### `v_system_region_summary` (view)
One row per OS region: committed vs resident vs Unity-tracked live, plus how much of the region's
resident RAM Unity explains. The region overview.

Columns: `region_index`, `name`, `addr_hex`, `committed_bytes`, `resident_bytes`, `pct_resident`,
`unity_alloc_count`, `unity_live_bytes`, `unity_live_pct_of_resident`.

```sql
-- Where is resident RAM, and how much does Unity account for?
SELECT name, committed_bytes, resident_bytes, pct_resident, unity_live_pct_of_resident
FROM v_system_region_summary ORDER BY resident_bytes DESC;
```

### `v_region_owner_breakdown` (view)
Within each OS region, who owns the allocations — by native type when the allocation's root has an
object, otherwise by area name.

Columns: `system_region_name`, `owner`, `alloc_count`, `live_bytes`.

```sql
SELECT * FROM v_region_owner_breakdown
WHERE system_region_name = 'MALLOC_NANO' ORDER BY alloc_count DESC;
```

### `v_connection_edges` (view)
The object reference graph with **both endpoints resolved** to type (and native name) — so you don't
re-join `connections` to `native_objects`/`managed_objects` every time. One row per edge; meant to be
**filtered** (the table is large), not selected wholesale.

Columns: `connection_type`, `from_kind`, `from_index`, `from_type`, `from_name`, `to_kind`,
`to_index`, `to_type`, `to_name`. (`*_name` is native-only; managed objects have no name.)

```sql
-- What does a specific object reference, by target type?
SELECT to_type, COUNT(*) FROM v_connection_edges
WHERE from_type = 'AssetBundle' AND to_kind = 'native_object' GROUP BY 1 ORDER BY 2 DESC;
```

### `v_assetbundle_utilization` (view)
One row per `AssetBundle` native object measuring whether it actually keeps loaded assets resident.
"References" counts outbound `native_connection` edges to **other** native objects (excluding the
bundle's self-reference and its own managed wrappers — no magic numbers). An *empty* bundle
(`references_loaded_assets = false`) is loaded but holds nothing live — usually reclaimable overhead.

Columns: `native_object_index`, `name`, `bundle_size_bytes`, `bundle_resident_bytes`, `is_destroyed`,
`referenced_object_count`, `referenced_type_count`, `referenced_size_bytes`,
`referenced_resident_bytes`, `references_loaded_assets`.

```sql
-- Utilization at a glance: empty vs. asset-holding bundles, and empty-bundle overhead.
SELECT references_loaded_assets,
       COUNT(*) AS bundles,
       ROUND(SUM(bundle_size_bytes) / 1048576.0, 1) AS bundle_mb
FROM v_assetbundle_utilization GROUP BY 1;

-- Which bundles reference the most other loaded objects (and how much do they pull in)?
SELECT name, referenced_object_count, referenced_type_count,
       ROUND(referenced_size_bytes / 1048576.0, 2) AS referenced_mb
FROM v_assetbundle_utilization
WHERE references_loaded_assets ORDER BY referenced_object_count DESC;
```

> `referenced_size_bytes` is the **own size of directly-referenced** native objects. Unity records
> flattened bundle→contained-object edges, so this is comprehensive for bundles; it is not transitive
> retained size, and the same shared asset may be counted under more than one bundle.

### `v_assetbundle_loaded_assets` (view)
The **exploded, per-asset companion** to `v_assetbundle_utilization`: one row per *(AssetBundle, loaded
native object)* pair — i.e. every asset an `AssetBundle` keeps loaded in memory, attributed to the
bundle that references it. Use it to **list the actual assets** held by bundles (the utilization view
only counts/sums them per bundle). It applies the **same edge filter** as `v_assetbundle_utilization`
(`native_object → native_object` `native_connection` edges with `to_index <> from_index`), so the
bundle's own native self-reference and its managed wrapper(s) are excluded — every row is a genuine
*other* loaded asset, with no magic numbers. A bundle that holds nothing (an "empty" bundle) produces
**no rows** here. A shared asset referenced by N bundles appears in N rows.

| Column | Type | Notes |
|--------|------|-------|
| `bundle_index` | INTEGER | The `AssetBundle`'s `native_objects.native_object_index`. |
| `bundle_name` | VARCHAR | The bundle's object name. |
| `bundle_size_bytes` | BIGINT | The bundle object's own native size. |
| `bundle_resident_bytes` | BIGINT | The bundle's resident bytes (format ≥ 17), else NULL. |
| `asset_index` | INTEGER | The loaded object's `native_objects.native_object_index`. |
| `asset_name` | VARCHAR | The loaded object's name. |
| `asset_type_name` | VARCHAR | The loaded object's native type (e.g. `Texture2D`, `Mesh`). |
| `asset_size_bytes` | BIGINT | The loaded object's own native size. |
| `asset_resident_bytes` | BIGINT | The loaded object's resident bytes (format ≥ 17), else NULL. |
| `asset_is_destroyed` | BOOLEAN | Loaded object marked destroyed but still resident. |

```sql
-- Every asset a specific bundle keeps loaded, biggest first.
SELECT asset_type_name, asset_name, ROUND(asset_size_bytes / 1048576.0, 2) AS asset_mb
FROM v_assetbundle_loaded_assets
WHERE bundle_name = 'characters' ORDER BY asset_size_bytes DESC;

-- What kinds of assets do bundles pull into memory, and how much?
SELECT asset_type_name, COUNT(*) AS assets,
       ROUND(SUM(asset_size_bytes) / 1048576.0, 1) AS total_mb
FROM v_assetbundle_loaded_assets GROUP BY 1 ORDER BY total_mb DESC;

-- Assets shared across multiple bundles (counted under each).
SELECT asset_name, asset_type_name, COUNT(DISTINCT bundle_index) AS bundles
FROM v_assetbundle_loaded_assets GROUP BY 1, 2 HAVING bundles > 1 ORDER BY bundles DESC;
```

> Like `v_assetbundle_utilization`, this reflects Unity's flattened bundle→contained-object edges:
> it is the set of **directly-referenced** loaded objects (comprehensive for bundles), not transitive
> retained reachability, and `asset_size_bytes` is each object's **own** size.

### `region_allocations(region_name)` (DuckDB macro)
All `v_allocation_enriched` rows for one OS region: `SELECT * FROM region_allocations('MALLOC_NANO');`

### `region_page_density(region_name)` (DuckDB macro)
Page-touch / fill analysis for a region, using `snapshot_info.page_size` (fallback 16384).

Columns: `touched_pages`, `touched_bytes`, `avg_live_bytes_per_page`, `avg_fill_pct`,
`avg_allocs_per_page`.

```sql
SELECT * FROM region_page_density('MALLOC_NANO');
```

> **Scope.** This metric is designed for **small-allocation zones** (`MALLOC_NANO`, `MALLOC_TINY`,
> `MALLOC_SMALL`) where each allocation fits within a page. It attributes every allocation to its
> *starting* page, so on a region containing allocations larger than a page (e.g. a dylib mapping or
> `MALLOC_LARGE`) `avg_fill_pct` can exceed 100% — a signal the model does not apply there, not a bug.

For a custom page size or page-spanning regions, query directly instead:

```sql
SELECT a.address >> 14 AS page_16k, COUNT(*), SUM(a.size_bytes)
FROM native_allocations a
JOIN system_memory_regions s
  ON s.name = 'MALLOC_NANO' AND a.address >= s.address AND a.address < s.address + s.size_bytes
GROUP BY 1;
```

---

## Relationships and join keys

```
native_objects.root_reference_id ──┐
                                    ├──► native_roots.root_id   (root_id UNIQUE; root↔object is 1:1 for area 'Objects')
native_allocations.root_reference_id ┘

native_allocations.memory_region_index ──► memory_regions.region_index   (Unity allocator bucket)
memory_regions.parent_region_index      ──► memory_regions.region_index   (hierarchy)
memory_regions.first_allocation_index   ──► native_allocations.allocation_index

managed_objects.native_object_index ──► native_objects.native_object_index   (C# wrapper ↔ native object)
connections.(from_kind,from_index) / (to_kind,to_index)  — object reference graph

system_memory_regions  — NO foreign key. Bridge by ADDRESS RANGE only
                         (a.address >= s.address AND a.address < s.address + s.size_bytes).
```

### The two region tables

| Table | What | Size field |
|-------|------|-----------|
| `memory_regions` | Unity's **internal allocator** buckets. `native_allocations.memory_region_index` points here. | `address_size` (often 0 — not a bound) |
| `system_memory_regions` | **OS virtual-memory** regions (vmmap). The RAM ground truth. No FK; bridge by address range. | `size_bytes` (committed), `resident_bytes` |

These overlap the same address space but are not linked by a key. Joining an allocation to its OS
region is exactly what `v_allocation_enriched` does for you.

### Gotchas

- **`native_object_address` ≠ `native_allocations.address`.** Objects and allocations are different
  layers; an object address never matches an allocation address. Bridge them through a shared
  **root** (`root_reference_id` → `root_id`), not by address.
- **Don't use `memory_regions.address_size` as a denominator.** Grouping allocators like
  `ALLOC_DEFAULT` report size 0 while holding most allocations. Use the allocation payload sum.
- **`system_memory_regions.type` is uniformly 0 on iOS.** Group/filter by `name`.
- **Resident data needs format ≥ 17.** Below that, `resident_size_bytes` and `page_size` are NULL.
- **The four "sizes" don't reconcile.** `system_memory_regions` (whole process VM), `native_roots`
  (Unity subsystem attribution), `native_allocations` (Unity allocator requests), and
  `native_objects` (high-level assets) are overlapping lenses, not a partition — never sum them.

---

## SQLite differences

- **Views**: identical names/columns; `v_allocation_enriched` resolves the OS region with a
  correlated subquery instead of `ASOF`.
- **Macros**: `region_allocations` and `region_page_density` are **not** available (SQLite has no
  table macros). Use the underlying views, or the direct query shown above.
- **Open read-only** for analysis: `Data Source=<path>;Mode=ReadOnly` (SQLite),
  `Data Source=<path>;ACCESS_MODE=READ_ONLY` (DuckDB). See [SQL safety](sql-safety.md).

---

## See also

- [SQL safety](sql-safety.md) — never build SQL from external data; parameterize.
- [Snap file format](snap-file-format.md) — where these tables come from in the `.snap` binary.
- [Architecture and design](design.md) — the export pipeline.

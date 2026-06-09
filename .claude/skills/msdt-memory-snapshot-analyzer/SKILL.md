---
name: msdt-memory-snapshot-analyzer
description: >
  AI-powered analysis of a Unity memory snapshot (.snap file or exported .duckdb/.db). Use when
  the user wants to understand memory usage, find memory hogs, diagnose leaks, get actionable
  recommendations, or get a plain-language summary of what's taking up memory. Triggers on phrases
  like "analyze this snapshot", "what's using all the memory", "find memory issues", "memory
  breakdown", "why is memory so high", or when a .snap/.duckdb/.db file is provided alongside
  any diagnostic intent. Always prefer this skill over asking the user to run queries manually.
---

# Memory Snapshot Analyzer

Produce an intelligent, plain-language analysis of a Unity memory snapshot. The goal is not
just to run the pipeline — it's to answer "what should I actually care about?" and give
actionable, prioritized findings.

The `memory-snapshot-report` skill handles the mechanics (export → validate → report). This
skill sits on top: it reads the exported database, runs diagnostic queries, and synthesizes
findings into a structured report with recommendations.

## Workflow

### 1. Resolve the input

The user may pass a `.snap`, an already-exported `.duckdb`/`.db`, or just a directory. Resolve
which you have:

- **`.snap`** → export it first (see step 2), then analyze the resulting DB.
- **`.duckdb` / `.db`** → skip to step 3 (check schema version first).
- **Directory** → find the most-recently-modified `.snap` or DB inside it.
- **Nothing explicit** → ask the user for a path before proceeding.

### 2. Export the snapshot (if needed)

Run from the repo root (`MemorySnapshotDataTools.sln` directory):

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -c Release -- \
  export "<snap-path>" "<output.duckdb>" --validate minimal --verbose
```

Default output path: same directory as the `.snap`, same basename, `.duckdb` extension.
If the export fails, report the error and stop — don't proceed with a corrupt DB.

### 3. Check schema version

```sql
SELECT schema_version_major, schema_version_minor, msdt_version, created_at_utc FROM schema_meta;
```

If the major version is behind current (see `memory-db-sql` skill), re-export. If the minor
version is behind, run `upgrade` in place first.

### 4. Run the diagnostic queries

Use `dotnet run -- summary "<db>"` for quick totals. For SQL queries, prefer a small Python script using the `duckdb` pip package (it's nearly always available) — the `duckdb` CLI binary is rarely in PATH. Then run the queries below.
Each query targets a specific concern. You don't need to run all of them every time — read
the questions the user has, pick the relevant ones, and add ad-hoc queries as needed.

**Snapshot overview**
```sql
SELECT product_name, platform, unity_version, snap_format_version, record_date_utc FROM snapshot_info;
```

**Memory category totals** — where the bytes actually go:
```sql
SELECT
  native_type_name,
  count(*)                         AS object_count,
  sum(size_bytes)                  AS total_bytes,
  round(sum(size_bytes)/1e6, 2)    AS total_mb
FROM native_objects
GROUP BY native_type_name
ORDER BY total_bytes DESC
LIMIT 30;
```

**Top individual objects by size:**
```sql
SELECT name, native_type_name, round(size_bytes/1e6,2) AS mb, is_destroyed
FROM native_objects
ORDER BY size_bytes DESC
LIMIT 20;
```

**Destroyed objects still resident (leak signal):**
```sql
SELECT native_type_name, count(*) AS count, round(sum(size_bytes)/1e6,2) AS mb
FROM native_objects
WHERE is_destroyed = true
GROUP BY native_type_name
ORDER BY mb DESC;
```

**Managed heap summary:**
```sql
SELECT type_name, count(*) AS instances, round(sum(size_bytes)/1e6,2) AS mb
FROM managed_objects
GROUP BY type_name
ORDER BY mb DESC
LIMIT 20;
```

**Asset bundle utilization (are any bundles loaded but barely used?):**
```sql
SELECT * FROM v_assetbundle_utilization ORDER BY live_asset_size_bytes ASC LIMIT 20;
```

**Native memory regions — biggest buckets:**
```sql
SELECT name, round(total_size_bytes/1e6,2) AS mb, object_count
FROM memory_regions
ORDER BY total_size_bytes DESC
LIMIT 20;
```

**System memory overview (OS-level, if available):**
```sql
SELECT * FROM v_system_region_summary ORDER BY committed_bytes DESC LIMIT 15;
```

### 5. Synthesize findings

Write a structured analysis report. Organize it into these sections (omit any section where
there's nothing interesting to say):

#### Memory snapshot overview
- Product name, platform, Unity version, capture timestamp.
- Top-line total committed memory and how it breaks down (native / managed / executable / graphics / untracked).

#### Top memory consumers
- List the 5–10 largest type categories with sizes in MB.
- Call out anything that looks abnormally large for the platform/game type.
- For textures: note if sizes suggest uncompressed or unmipmapped assets.
- For meshes: flag unusually large counts or sizes.

#### Potential memory leaks
- Destroyed objects still resident — list by type with total MB.
- Any individual object with an outsized footprint that looks like it should have been unloaded.

#### Asset bundle health (if data is available)
- Bundles with low utilization relative to their loaded size.
- Bundles that reference many other loaded assets (potential load cascade).

#### Managed heap health
- Total managed heap size and fragmentation if inferrable.
- Top C# types by instance count or size.
- GC pressure signals: large arrays, many small allocations.

#### Recommendations (prioritized)
Number these from highest to lowest impact. Be specific — name the object types or bundles.
Example format:
```
1. [HIGH] 230 MB of Texture2D objects have is_destroyed=true — ensure Destroy() calls are
   paired with proper asset bundle unloading. Start with the largest destroyed textures above.
2. [MEDIUM] AudioClip takes 140 MB across 512 objects — verify clips marked for streaming are
   not being loaded into memory in full.
3. [LOW] 3 asset bundles have utilization < 5% — consider lazy-loading or merging.
```

### 6. Offer follow-up

After the analysis, offer:
- "Want me to generate the full HTML report? (run `memory-snapshot-report` skill)"
- "Want to dig into a specific type or bundle with a custom SQL query? (run `memory-db-sql` skill)"
- "I can compare this snapshot against another if you have a second `.snap` or `.duckdb`."

## Platform-specific context

Apply this when interpreting results:

| Platform | Notes |
|----------|-------|
| iOS (IPhonePlayer) | Page size 16 KB on arm64. `system_memory_regions.type` is always 0 — group by `name` instead. Graphics memory is estimated, not measured. |
| Android | Page size 4 KB. Texture compression format matters; ASTC expected for modern devices. |
| PC / Mac | Higher memory budgets; flag anything > 1 GB total as potentially problematic for mid-range targets. |
| Console | Platform-specific budgets; defer to user's stated limits. |

## Common gotchas (from `memory-db-sql` skill)

- `native_object_address` ≠ `native_allocations.address` — never join on address.
- Two region tables: `memory_regions` (Unity allocator buckets) vs `system_memory_regions` (OS VM). Bridge by address range, not by key.
- Resident data requires `snap_format_version ≥ 17`; columns are NULL otherwise.
- Don't count self-reference edges when analyzing connections — use `v_assetbundle_utilization` instead of hand-rolling connection counts.

## Tone

Be direct and practical. Quantify everything in MB (or GB where appropriate). Don't hedge with
"this might possibly be" — if the data shows 230 MB of destroyed textures, say so. If the data
is ambiguous, say why and what additional information would resolve it.

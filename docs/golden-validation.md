# Golden value validation

Golden validation answers one question: **does the database this tool exports from a `.snap`
agree with what Unity's own Memory Profiler reports for the same snapshot?** You capture
reference ("golden") numbers from inside Unity, then ask the CLI to re-derive the same numbers
from an exported database and diff the two within set tolerances.

Use it to catch export/parsing regressions — if a schema, query, or extraction change quietly
shifts a size or count, validation fails with the exact metric and the expected/actual values.

## End-to-end workflow

```
Unity Editor                          MemorySnapshotDataTools CLI
────────────                          ───────────────────────────
.snap ──(Memory Profiler)──► {name}_golden.json
.snap ──────────────────────────────► export ──► {name}.duckdb / .db
                                       validate {name}_golden.json {name}.duckdb
                                              │
                                              ▼
                                       {name}_golden_validation_result.json  (+ exit code)
```

The golden file and the database **must come from the same `.snap`**, or every metric will
differ.

> The CLI examples below call `MemorySnapshotDataTools` directly. Build it once with
> `dotnet build -c Release` and put `Cli/bin/Release/net10.0/<RID>` on your `PATH` (see the
> README's *How to use*), or run from source with
> `dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- <command>`.

## Step 1 — Extract golden values in Unity

The extractor is a separate Unity Editor package, **not** part of the .NET CLI:

- **Package:** `com.unity.memory-snapshot-data-tools` (v0.2.0), in
  [`UnityPackage/`](https://github.com/Unity-Technologies/MemorySnapshotDataTools/tree/main/UnityPackage)
  of this repo. Requires Unity **2022.3+** and `com.unity.memoryprofiler` **1.1.12**.
- **Import:** add it to the target Unity project's `Packages/manifest.json` via a local `file:`
  path pointing at `UnityPackage/com.unity.memory-snapshot-data-tools`.
- **Run:** **Tools → Memory Snapshot Validation → Extract Golden Values**, then pick a `.snap`.
  It writes `{name}_golden.json` next to the snapshot and reveals it in the file browser. The
  Console logs a summary of the extracted metrics.

Under the hood the extractor loads the snapshot through the Memory Profiler, reads
`ProcessedNativeRoots`, and invokes the Memory Profiler's **own** summary model builders
(`AllMemorySummaryModelBuilder`, `ManagedMemorySummaryModelBuilder`,
`ResidentMemorySummaryModelBuilder`). That is deliberate: the golden Summary-page numbers are
produced by the same code as the Memory Profiler UI, so a passing validation means the tool
matches what a developer sees in the profiler — not just an independent re-implementation.

If Summary-model extraction fails for a snapshot, the native metrics are still written and the
summary arrays are left empty. Such a golden file is still valid; the tool simply skips the
Summary comparison for it (see [backward compatibility](#backward-compatibility)).

## Step 2 — Export the same snapshot to a database

```bash
MemorySnapshotDataTools export <name>.snap <name>.duckdb --validate minimal
```

DuckDB (`.duckdb`) is recommended; SQLite (`.db`, with `--destination sqlite`) also works.
Validation needs the `native_objects`, `native_roots`, and `summary_metrics` tables, which a
current-schema export produces.

## Step 3 — Run `validate`

```bash
MemorySnapshotDataTools validate <name>_golden.json <name>.duckdb [--out result.json]
```

- The database is checked against the current schema version first; a database from an older
  **major** schema (missing tables/columns validation needs) is rejected — re-export it.
- The result JSON is written next to the golden file as `{name}_golden_validation_result.json`
  unless `--out` is given. The full result is also printed to stdout.
- **Exit codes:** `0` = passed, `1` = one or more metric mismatches, `3` = error (bad input,
  unparseable golden JSON, unsupported database extension, etc.).

## What gets compared

| Metric | Golden source | Exported-DB source | Tolerance |
|--------|---------------|--------------------|-----------|
| `AssetBundle` Count / Allocated | `NativeTypeMetrics[AssetBundle]` | `native_objects` where `native_type_name='AssetBundle'` and not destroyed: `COUNT`, `SUM(size_bytes)` | exact |
| `AssetBundle` Resident | same | `SUM(resident_size_bytes)` | resident |
| `SerializedFile` Count / Allocated | `NativeTypeMetrics[SerializedFile]` | `native_roots` where `area_name LIKE '%serializedfile%'`: `COUNT`, `SUM(accumulated_size_bytes)` | exact |
| `SerializedFile` Resident | same | `SUM(resident_size_bytes)` | resident |
| `PMR` Allocated | sum of `NativeRootMetrics[*].AllocatedBytes` | sum of `native_roots` Remapper / `PersistentManager…Remapper` rows' `accumulated_size_bytes` | exact |
| `PMR` Resident | sum of `NativeRootMetrics[*].ResidentBytes` | sum of those rows' `resident_size_bytes` | resident |
| `Summary.TotalAllocated` / `TotalResident` | `TotalAllocatedBytes` / `TotalResidentBytes` | `summary_metrics` Totals row | committed / resident |
| `Summary[AllocatedMemoryDistribution].*` | `AllocatedMemoryDistribution[]` | `summary_metrics` group rows | committed / resident |
| `Summary[ManagedHeapUtilization].*` | `ManagedHeapUtilization[]` | `summary_metrics` group rows | committed / resident |

"PMR" = PersistentManager Remapper; the golden side lists individual Remapper roots and the tool
compares their **sum**, not each row.

### Tolerances

Memory accounting diverges slightly between Unity's full memory-map post-processing and the
exported tables, so non-counting metrics allow a small delta. A value passes when it is within
`max(absolute, relative × max(|expected|, |actual|))`:

| Comparison | Rule |
|------------|------|
| Counts (`*.Count`) and allocated bytes for tracked types / PMR | **exact** — must be equal |
| Resident bytes (everywhere) | `max(64 KB, 1%)` |
| Summary committed — normal rows | `max(64 KB, 1%)` |
| Summary committed — **estimated** rows (Graphics, Untracked) | `max(1 MB, 5%)` |

A row is "estimated" when its golden `ResidentAvailable` is `false` (Graphics and Untracked are
derived from platform stats, not measured directly). Resident is only compared when **both**
golden and exported rows have resident available. The Memory Profiler labels Untracked as
`Untracked*`; the trailing `*` is tolerated on category-name matching.

### Backward compatibility

The Summary comparison is skipped entirely when the golden file has no
`AllocatedMemoryDistribution` and no `ManagedHeapUtilization` rows. Older golden files (and any
where Summary extraction failed in Unity) therefore still validate on the native metrics alone.

## Golden JSON schema

Produced by `GoldenValueExtractor`; consumed by `GoldenSnapshotFile`.

```jsonc
{
  "SnapshotName": "MyGame",
  "SnapshotPath": "/path/to/MyGame.snap",
  "FormatVersion": 17,
  "ExtractedAtUtc": "2026-01-01T00:00:00.0000000Z",
  "NativeTypeMetrics": [
    { "NativeTypeName": "AssetBundle",    "Count": 12, "AllocatedBytes": 0, "ResidentBytes": 0 },
    { "NativeTypeName": "SerializedFile", "Count": 34, "AllocatedBytes": 0, "ResidentBytes": 0 }
  ],
  "NativeRootMetrics": [
    { "AreaName": "PersistentManager.Remapper", "ObjectName": "Remapper", "AllocatedBytes": 0, "ResidentBytes": 0 }
  ],
  "TotalAllocatedBytes": 0,
  "TotalResidentBytes": 0,
  "AllocatedMemoryDistribution": [
    { "Name": "Native",               "CommittedBytes": 0, "ResidentBytes": 0, "ResidentAvailable": true  },
    { "Name": "Managed",              "CommittedBytes": 0, "ResidentBytes": 0, "ResidentAvailable": true  },
    { "Name": "Executables & Mapped", "CommittedBytes": 0, "ResidentBytes": 0, "ResidentAvailable": true  },
    { "Name": "Graphics (Estimated)", "CommittedBytes": 0, "ResidentBytes": 0, "ResidentAvailable": false },
    { "Name": "Untracked",            "CommittedBytes": 0, "ResidentBytes": 0, "ResidentAvailable": false }
  ],
  "ManagedHeapUtilization": [
    { "Name": "Virtual Machine",   "CommittedBytes": 0, "ResidentBytes": 0, "ResidentAvailable": true },
    { "Name": "Objects",           "CommittedBytes": 0, "ResidentBytes": 0, "ResidentAvailable": true },
    { "Name": "Empty Heap Space",  "CommittedBytes": 0, "ResidentBytes": 0, "ResidentAvailable": true }
  ]
}
```

## Validation result JSON

Produced by `GoldenValidationResult`:

```jsonc
{
  "SnapshotName": "MyGame",
  "GoldenPath": "/path/to/MyGame_golden.json",
  "DatabasePath": "/path/to/MyGame.duckdb",
  "ValidatedAtUtc": "2026-06-05T12:00:00.0000000Z",
  "Passed": false,
  "Failures": [
    "SerializedFile.Count: expected=34, actual=33",
    "Summary[AllocatedMemoryDistribution].Native.Committed: expected=5000000, actual=9000000"
  ]
}
```

### Failure string formats

Each entry in `Failures` is one of:

- `{Type}.Count` / `{Type}.AllocatedBytes` / `{Type}.ResidentBytes` — for `AssetBundle`, `SerializedFile`.
- `PMR.AllocatedBytes` / `PMR.ResidentBytes`.
- `Summary.TotalAllocated` / `Summary.TotalResident`, or `Summary.Total: row missing from export`.
- `Summary[{group}].{name}.Committed` / `Summary[{group}].{name}.Resident`, or
  `Summary[{group}].{name}: row missing from export` — where `{group}` is
  `AllocatedMemoryDistribution` or `ManagedHeapUtilization`.

All comparison failures carry `expected=…, actual=…`.

## Troubleshooting

- **Everything mismatches** → the golden file and the database came from *different* snapshots,
  or different captures of the same scene. Re-extract and re-export from one `.snap`.
- **`Summary.* row missing from export`** → the export predates the `summary_metrics` table, or
  the golden file has Summary rows the export lacks. Re-export with the current tool.
- **Schema-gate rejection on `validate`** → the database is from an older major schema. Re-export
  from the `.snap` (an in-place `upgrade` only covers minor analysis-view changes, not new
  tables/columns validation depends on).
- **`Unsupported database extension`** → pass a `.duckdb` or `.db` file, not a `.snap`.
- **Golden has empty Summary arrays** → Summary extraction failed in Unity (a `Debug.LogWarning`
  was logged there). Native metrics still validate; re-extract in Unity to get Summary coverage.

## Keeping the two sides in sync

The Unity extractor and the .NET tool intentionally share definitions so they don't drift:

- Tracked type names (`AssetBundle`, `SerializedFile`) and the SerializedFile area predicate are
  defined on both sides — `MemorySnapshotValidationHelpers` (Unity) and
  `GoldenValidationQueries` (tool). Change them together.
- The category names and `ResidentAvailable` semantics mirror the Memory Profiler Summary rows;
  the tool reads them from the export's `summary_metrics` table.

The validation queries are constants in `GoldenValidationQueries` (no external values are
interpolated into SQL), per the repo's [SQL safety rules](sql-safety.md). For the database tables
and columns referenced above, see the [database schema](database-schema.md).

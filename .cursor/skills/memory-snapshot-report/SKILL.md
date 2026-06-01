---
name: memory-snapshot-report
description: Generate and view Unity memory snapshot reports. Use when the user wants to analyze a Unity memory snapshot, export it to a database, or generate/view an HTML report.
---

# Memory Snapshot Report

## When to use

- User wants to analyze a Unity memory snapshot (`.snap` file).
- User wants to export a snapshot to a DuckDB or SQLite database.
- User wants to generate or view an HTML report from an exported snapshot database.

## Prerequisites

- .NET 10 SDK.
- Project path: **MemorySnapshotDataTools** is the project root; run commands from that directory.

## Steps

### 1. Export snapshot to database

From the MemorySnapshotDataTools directory:

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- export <path/to/snapshot.snap> <path/to/output.duckdb> --validate minimal --verbose
```

- Use `.duckdb` for DuckDB (recommended) or `.db` for SQLite.
- For SQLite add `--destination sqlite`.
- `--verbose` prints progress and timings (parse+extract vs. write).

**Batch export** every `.snap` in a directory to `<basename>.duckdb` alongside each file:

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- batch-export <directory> \
  --filter MyGame \
  --skip-existing \
  --verbose
```

- `--filter` is optional (case-insensitive substring on filenames).
- `--skip-existing` skips when the output DB is newer than the snap.
- Exit code `0` = all succeeded, `1` = one or more failures, `2` = cancelled.

### 2. Validate export against Unity golden JSON

The golden extractor lives in the `com.unity.memory-snapshot-data-tools` package under `UnityPackage/`
in this repo, imported into U6TestBed via a local `file:` path in `Packages/manifest.json`. After extracting
`*_golden.json` in Unity (**Tools → Memory Snapshot Validation → Extract Golden Values**):

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- validate \
  <path/to/snapshot_golden.json> \
  <path/to/exported.duckdb>
```

- Compares AssetBundle, SerializedFile (Unity Subsystems native roots), and Remapper metrics.
- Also compares the MemoryProfiler **Summary** page metrics from the `summary_metrics` table:
  - *Allocated Memory Distribution*: Total Allocated, Total Resident, Native, Managed,
    Executables & Mapped, Graphics (Estimated), Untracked.
  - *Managed Heap Utilization*: Virtual Machine, Objects, Empty Heap Space.
  - Committed bytes use a 1% / 64 KB tolerance (5% / 1 MB for the estimated Graphics and Untracked rows);
    resident bytes use 1% / 64 KB and are skipped for Graphics and Untracked (resident unavailable).
- Writes `*_validation_result.json` next to the golden file unless `--out` is set.
- Exit code `0` = pass, `1` = metric mismatch, `3` = error.

### 3. Generate HTML report

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- report <path/to/output.duckdb> --out report.html --verbose
```

- Omit `--out` to write to a temp file and open in the browser.
- Use `--title "My Report"` to set the report title.
- Report works with either DuckDB or SQLite databases produced by the export command.

### 4. Optional

- Open the generated HTML file or DB in the user’s preferred viewer.
- For ad-hoc SQL, use the same DB path; tables include `snapshot_info`, `native_objects`, `managed_objects`, `connections`, `native_roots`, `memory_regions`, `native_allocations`, `system_memory_regions`, and `summary_metrics` (MemoryProfiler Summary-page breakdown).

## Domain

- The tool supports **DuckDB** (default) and **SQLite**; report can be generated from either.
- The CLI reports **timings**: export shows parse+extract vs. write; report shows query vs. render vs. write. Use `--verbose` to see them.

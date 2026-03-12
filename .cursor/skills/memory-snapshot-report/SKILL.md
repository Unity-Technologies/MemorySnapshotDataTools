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

### 2. Generate HTML report

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- report <path/to/output.duckdb> --out report.html --verbose
```

- Omit `--out` to write to a temp file and open in the browser.
- Use `--title "My Report"` to set the report title.
- Report works with either DuckDB or SQLite databases produced by the export command.

### 3. Optional

- Open the generated HTML file or DB in the user’s preferred viewer.
- For ad-hoc SQL, use the same DB path; tables include `snapshot_info`, `native_objects`, `managed_objects`, `connections`, `native_roots`, `memory_regions`, `native_allocations`.

## Domain

- The tool supports **DuckDB** (default) and **SQLite**; report can be generated from either.
- The CLI reports **timings**: export shows parse+extract vs. write; report shows query vs. render vs. write. Use `--verbose` to see them.

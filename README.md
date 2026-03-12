# Memory Snapshot Data Tools

Single CLI to **export** Unity memory snapshots (`.snap`) to DuckDB or SQLite and **generate** HTML reports from those databases.

## What it does

- **Export:** Reads a `.snap` file, parses and extracts snapshot data, and writes it to a DuckDB (default) or SQLite file.
- **Report:** Connects to an exported database (DuckDB or SQLite), runs report queries, and produces a self-contained HTML report with sortable tables.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## How it works

- **Cli** (exe): entry point and options; **Core** (library): Parser (extraction), Export + ExportDestination (write DBs), Report (query + render). Shared data lives in Core (Models).
- **Export:** reads `.snap` via Parser, extracts rows (SnapshotBridge), writes to DuckDB or SQLite via a producer/consumer pipeline.
- **Report:** opens the DB with Report/Queries backend, runs SQL, builds ReportModel, renders HTML (ReportRenderer + ReportHtmlHelper).

## How to use

Use the **MemorySnapshotDataTools** directory as the project root. Run the CLI with the Cli project:

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- <command> [args...]
```

Or from the `Cli` directory: `dotnet run -- <command> [args...]`.

### Export a snapshot to a database

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- export <path/to/snapshot.snap> <path/to/output.duckdb> [options]
```

- Use a `.duckdb` extension for DuckDB (default) or `.db` for SQLite.
- **Options:** `--destination duckdb|sqlite`, `--validate none|minimal|full`, `--verbose` (progress and timings).

**Example (DuckDB):**

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- export ./memory.snap ./out.duckdb --validate minimal --verbose
```

**Example (SQLite):**

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- export ./memory.snap ./out.db --destination sqlite --validate minimal --verbose
```

### Generate a report from a database

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- report <path/to/database.duckdb|.db> [--out report.html] [options]
```

- **`--out`** path: where to write the HTML file. If omitted, writes to a temp file and opens it in the browser.
- **`--title "Title"`:** report title (default: "Memory Snapshot Report").
- **`--verbose`:** print timings (query, render, write).

**Example:**

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- report ./out.duckdb --out report.html --verbose
```

## Output

- **Export:** Creates a `.duckdb` or `.db` file with tables: `snapshot_info`, `native_objects`, `managed_objects`, `connections`, `native_roots`, `memory_regions`, `native_allocations`.
- **Report:** Produces one HTML file with navigation, sections (Snapshot Info, Native Objects, Managed Heap, Roots, Regions, Connections), and sortable tables.
- **Timings:** With `--verbose`, export prints parse+extract vs. write; report prints query vs. render vs. write and a one-line summary (e.g. `Report completed in 2.3s (query 1.1s, render 0.5s, write 0.1s)`).

## Schema (for ad-hoc queries)

| Table               | Description                                      |
|---------------------|--------------------------------------------------|
| `snapshot_info`     | Snapshot path, export timestamp, Unity version   |
| `native_objects`    | Native Unity objects (size, type, name)          |
| `managed_objects`   | Managed heap objects (address, type, size)       |
| `connections`       | Edges: from_kind/from_index → to_kind/to_index   |
| `native_roots`      | Root references and accumulated size            |
| `memory_regions`    | Native memory regions (address, size, hierarchy) |
| `native_allocations`| Allocations within regions                       |

Use any DuckDB or SQLite client to query these tables.

## Build and test

From the project root:

```bash
dotnet build
dotnet test
```

To run the CLI: `dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj --` or publish the Cli project (see below).

## Publish (versioned artifacts)

From the project root, run `./publish.sh` (macOS/Linux) or `./publish.ps1` (Windows). These publish the **Cli** project and produce `artifacts/MemorySnapshotDataTools-<Version>-<RID>.zip` for each runtime (win-x64, linux-x64, osx-x64, osx-arm64).

## AI IDE integration

A project skill for Cursor (and similar AI IDEs) is in `.cursor/skills/memory-snapshot-report/`. It describes the export and report workflow and when to use it.

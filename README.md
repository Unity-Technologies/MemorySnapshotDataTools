# Memory Snapshot Data Tools

Single CLI to **export** Unity memory snapshots (`.snap`) to DuckDB or SQLite and **generate** HTML reports from those databases.

## What it does

The tool is a single CLI with these commands:

- **`export`:** Read a `.snap` file, parse and extract its data, and write it to a DuckDB (default) or SQLite database.
- **`batch-export`:** Export every `.snap` in a directory to its own database with the same basename.
- **`report`:** Run report queries against one exported database and produce a self-contained HTML report with sortable tables.
- **`multi-report`:** Produce a single HTML report comparing multiple exported databases in a directory.
- **`validate`:** Compare an exported database against a Unity golden JSON file.
- **`summary`:** Print a high-level memory-usage summary for a `.snap` or database (writes no database).
- **`upgrade`:** Upgrade an exported database's analysis views/indexes to the current schema version, in place.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## How it works

- **Cli** (exe): entry point and options; **Core** (library): Parser (extraction), Export + ExportDestination (write DBs), Report (query + render). Shared data lives in Core (Models).
- **Export:** reads `.snap` via Parser, extracts rows (SnapshotBridge), writes to DuckDB or SQLite via a producer/consumer pipeline.
- **Report:** opens the DB with Report/Queries backend, runs SQL, builds ReportModel, renders HTML (ReportRenderer + ReportHtmlHelper).

## How to use

Build a Release binary once, then add its output directory to your `PATH` so you can invoke `MemorySnapshotDataTools` directly:

```bash
dotnet build -c Release
# Add the build output to PATH. <RID> is your runtime: osx-arm64, osx-x64, linux-x64, or win-x64.
export PATH="$PATH:$(pwd)/Cli/bin/Release/net10.0/<RID>"
MemorySnapshotDataTools <command> [args...]
```

Add that `export PATH=...` line to your shell profile to make it permanent. All examples below use `MemorySnapshotDataTools <command>`.

> Working from source without installing? Run `dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- <command>` from the repo root instead.

### Export a snapshot to a database

```bash
MemorySnapshotDataTools export <path/to/snapshot.snap> <path/to/output.duckdb> [options]
```

- Use a `.duckdb` extension for DuckDB (default) or `.db` for SQLite.
- **Options:** `--destination duckdb|sqlite`, `--validate none|minimal|full`, `--verbose` (progress and timings).

**Example (DuckDB):**

```bash
MemorySnapshotDataTools export ./memory.snap ./out.duckdb --validate minimal --verbose
```

**Example (SQLite):**

```bash
MemorySnapshotDataTools export ./memory.snap ./out.db --destination sqlite --validate minimal --verbose
```

### Batch-export a directory of snapshots

```bash
MemorySnapshotDataTools batch-export <path/to/directory> [options]
```

- Exports every top-level `.snap` in the directory to a `.duckdb`/`.db` file with the same basename.
- **`--filter <substr>`:** case-insensitive substring filter on snapshot filenames (e.g. `MyGame`).
- **`--skip-existing`:** skip a file when its output database exists and is newer than the `.snap`.
- **`--continue-on-error`:** keep going after a single-file failure (default: `true`).
- Also accepts the same `--destination`, `--validate`, `--batch-size`, `--queue-capacity`, and `--verbose` options as `export`.

**Example:**

```bash
MemorySnapshotDataTools batch-export ./captures --filter MyGame --skip-existing --verbose
```

### Generate a report from a database

```bash
MemorySnapshotDataTools report <path/to/database.duckdb|.db> [--out report.html] [options]
```

- **`--out`** path: where to write the HTML file. If omitted, writes to a temp file and opens it in the browser.
- **`--title "Title"`:** report title (default: "Memory Snapshot Report").
- **`--verbose`:** print timings (query, render, write).

**Example:**

```bash
MemorySnapshotDataTools report ./out.duckdb --out report.html --verbose
```

### Generate a report across multiple databases

```bash
MemorySnapshotDataTools multi-report <path/to/directory> [--out report.html] [options]
```

- Builds one HTML report comparing every `.duckdb`/`.db` snapshot database in the directory.
- **`--filter <substr>`:** case-insensitive substring filter on database filenames (e.g. `MyGame`).
- **`--out`** path: where to write the HTML file. If omitted, writes to a temp file and opens it in the browser.
- **`--title "Title"`:** report title (default: "Multi-Snapshot Memory Report").
- **`--no-reports`:** skip generating per-snapshot drill-down reports (faster; rows are not clickable).
- **`--verbose`:** print progress and timings.

**Example:**

```bash
MemorySnapshotDataTools multi-report ./databases --out multi.html --verbose
```

### Validate an export against Unity golden values

```bash
MemorySnapshotDataTools validate <path/to/*_golden.json> <path/to/database.duckdb|.db> [--out result.json]
```

- Compares the exported database against a `*_golden.json` produced by Unity's GoldenValueExtractor.
- **`--out`** path: where to write the validation result JSON (default: next to the golden file).
- Exit codes: `0` = passed, `1` = metric mismatch(es), `3` = error.
- For the full workflow — extracting the golden file in Unity, what each metric compares, and the
  tolerances — see [docs/golden-validation.md](docs/golden-validation.md).

**Example:**

```bash
MemorySnapshotDataTools validate ./memory_golden.json ./out.duckdb
```

### Print a memory-usage summary

```bash
MemorySnapshotDataTools summary <path/to/snapshot.snap | database.duckdb|.db> [--verbose]
```

- Prints a high-level memory breakdown to the console. Accepts **either** a `.snap` snapshot or an exported database, and writes **no** database. (Decoding a raw `.snap` is slower than reading a database.)
- **`--verbose`:** print progress while decoding a snapshot.

**Example:**

```bash
MemorySnapshotDataTools summary ./out.duckdb
```

### Upgrade a database's schema in place

```bash
MemorySnapshotDataTools upgrade <path/to/database.duckdb|.db>
```

- Upgrades an exported database's analysis views/indexes to the current minor schema version, in place — no re-export needed. If the database's major version is behind, it reports that a re-export is required instead.

**Example:**

```bash
MemorySnapshotDataTools upgrade ./out.duckdb
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
| `memory_regions`    | Native memory regions (address, reserved size, resident bytes, hierarchy) |
| `native_allocations`| Allocations within regions                       |

Use any DuckDB or SQLite client to query these tables.

### Reserved vs. allocated vs. resident

Three distinct measurements apply to a native memory region (`memory_regions`) — don't confuse them:

- **Reserved** — `memory_regions.address_size`: address space the allocator asked the OS for. Reserving costs no physical memory by itself.
- **Allocated (live)** — `SUM(native_allocations.size_bytes)` for allocations in the region: bytes actively handed out to callers.
- **Resident** — `memory_regions.resident_bytes`: whole OS pages actually backed by physical RAM at snapshot time. This is what counts against the process RSS. Measured at page granularity (`snapshot_info.page_size`, typically 4–16 KiB); a page counts if any of it is backed, partial edge pages are trimmed. `NULL` when unknown (snapshot format < 17 has no residency bitmap, or the region reserves nothing).

`resident_bytes` is populated per region, but **parent regions overlap their children**, so summing it over
every row double-counts. To total resident RAM safely, sum over the leaf-only view **`v_leaf_region_resident`**
(exposes `region_index`, `name`, `address_base`, `address_size`, `resident_bytes`, and `resident_pct`).

## Build and test

From the project root:

```bash
dotnet build
dotnet test
```

To run the CLI after building, put `Cli/bin/Release/net10.0/<RID>` on your `PATH` (see [How to use](#how-to-use)), or publish the Cli project (see below).

## Publish (versioned artifacts)

From the project root, run `./publish.sh` (macOS/Linux) or `./publish.ps1` (Windows). These publish the **Cli** project and produce `artifacts/MemorySnapshotDataTools-<Version>-<RID>.zip` for each runtime (win-x64, linux-x64, osx-x64, osx-arm64).

## AI IDE integration

Project skills for Claude (and similar AI IDEs) are in `.claude/skills/**`.

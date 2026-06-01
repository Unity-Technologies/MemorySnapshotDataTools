---
name: run-memory-snapshot-data-tool
description: Build, run, screenshot, and test the Memory Snapshot Data Tool — the .NET 10 CLI that exports Unity .snap memory snapshots to DuckDB/SQLite and renders an HTML report. Use when asked to run, start, build, or test the tool, export a snapshot, generate or screenshot a report, or confirm a change works in the real app.
---

# Run: Memory Snapshot Data Tool

A .NET 10 CLI (`MemorySnapshotDataTools`) that exports a Unity memory snapshot (`.snap`)
to a DuckDB/SQLite database, then runs SQL to render a self-contained **HTML report**.
The report is the real product, so the agent path drives the whole pipeline and
**screenshots the rendered report with headless Chrome** — a CLI run you can look at.

The driver is [smoke.sh](.claude/skills/run-memory-snapshot-data-tool/smoke.sh):
`build → export → summary → report → screenshot`, with a pass/fail check at each step.

**Paths below are relative to the repo root** (the directory containing `MemorySnapshotDataTools.sln`).

## Prerequisites

- **.NET 10 SDK** — required. Verify: `dotnet --version` (10.0.103 here).
- **A `.snap` snapshot** — required, and **NOT in this repo**. Captures are large
  (16 MB–600+ MB) and user-specific. Get one from the Unity **Memory Profiler**
  ("Capture") or point at one you already have. You pass its path to the driver.
- **Google Chrome** — optional, only for the screenshot step. Expected at
  `/Applications/Google Chrome.app/Contents/MacOS/Google Chrome`. Without it the
  driver still runs and writes the HTML; it just skips the PNG.

## Build

```bash
dotnet build MemorySnapshotDataTools.sln -c Release
```

## Run (agent path) — the driver

The snapshot is required; supply it as an argument (or via `MSDT_SNAP`):

```bash
.claude/skills/run-memory-snapshot-data-tool/smoke.sh /path/to/snapshot.snap
```

End-to-end on a 16 MB snapshot this takes **~8s** (most of it the build). Exit code `0`
means every step passed. Artifacts land in `/tmp/msdt-run/`:

- `out.duckdb` — the exported database
- `summary.txt` — the `summary` command's text output
- `report.html` — the rendered report
- `report.png` — **headless-Chrome screenshot of the report — open/Read this to see it**

Useful env overrides (see the header of [smoke.sh](.claude/skills/run-memory-snapshot-data-tool/smoke.sh)):

- `MSDT_SNAP_DIR=/path/to/captures` — auto-pick the **smallest** `.snap` in a directory
  (handy when you don't care which snapshot; smallest = fastest).
- `MSDT_OUT_DIR=/some/dir` — write artifacts elsewhere (default `/tmp/msdt-run`).
- `MSDT_NO_BUILD=1` — skip the build (you just built).
- `MSDT_SQLITE=1` — also export+report via the SQLite backend. **Slow** — see Gotchas.

## Direct CLI invocation

To run one command without the driver (`export`/`report`/`summary`/`batch-export`/
`multi-report`/`validate`):

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -c Release -- --help
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -c Release -- export /path/to/snapshot.snap /tmp/out.duckdb --validate minimal
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -c Release -- summary /tmp/out.duckdb
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -c Release -- report /tmp/out.duckdb --out /tmp/report.html --title "My Report"
```

- `.duckdb` extension → DuckDB (default); `.db` + `--destination sqlite` → SQLite.
- `summary` accepts **either** a `.snap` or an exported DB and prints a memory breakdown
  without writing a database (decoding a raw `.snap` is slower than reading a DB).
- `report --out` is optional; omit it and the tool writes a temp file and tries to open it
  in a browser (don't rely on the auto-open when running headless — always pass `--out`).

## Run (human path)

`dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- report foo.duckdb` with no
`--out` opens the HTML in your default browser. Fine interactively; useless headless — use
the driver or pass `--out` instead.

## Test

```bash
dotnet test MemorySnapshotDataTools.sln -c Release --no-build
```

19 tests, ~0.5s. (Build first, or drop `--no-build`.)

## Gotchas

- **SQLite report is ~1000× slower than DuckDB.** On the same 16 MB snapshot, the DuckDB
  `report` query was **0.1s**; the SQLite `report` query was **146s**. Export is fast for
  both (~1s). Default to DuckDB; only set `MSDT_SQLITE=1` when you specifically need to
  exercise the SQLite path, and expect it to take minutes.
- **Snapshots are not in the repo and are huge.** The driver fails fast with a clear message
  if you don't pass one.
- **`report` opens the DB read-only** (defense-in-depth, per `CLAUDE.md`), so reporting can't
  mutate the database — but a *second* process holding a write lock on a DuckDB file can still
  block it. If you hit a DuckDB lock error on a pre-existing `.duckdb`, a GUI client
  (e.g. DataGrip) probably has it open — close it rather than copying the file. The driver
  sidesteps this by exporting a fresh DB into `/tmp/msdt-run`.
- **CLI exit codes are meaningful:** `0` success, `1` bad args / file not found,
  `2` cancelled (Ctrl-C), `3` validation/error (for `validate`). The driver asserts on them.
- **The built CLI lives in a RID-specific folder** (`Cli/bin/Release/net10.0/osx-arm64/`).
  The driver globs for `MemorySnapshotDataTools.dll` so it doesn't hard-code the RID.

## Troubleshooting

- `SMOKE FAIL: No .snap provided` — pass a snapshot path, or set `MSDT_SNAP` / `MSDT_SNAP_DIR`.
- `SMOKE FAIL: CLI dll not found under Cli/bin/Release` — you set `MSDT_NO_BUILD=1` without
  having built. Build first, or unset it.
- `WARN: screenshot not produced` / `Chrome not found` — Chrome isn't at the expected path;
  the HTML is still in `MSDT_OUT_DIR`. Open it in any browser.

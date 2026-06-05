---
name: validate-golden
description: Validate a Memory Snapshot Data Tool export against Unity Memory Profiler "golden" values — extract a {name}_golden.json in Unity, export the same .snap to a database, run the `validate` CLI command, and interpret the pass/fail result, tolerances, and failures. Use when asked to validate/verify an export, check it matches Unity's Memory Profiler, produce or read a golden file, debug a validation failure, or change what golden validation compares.
---

# Validate an export against Unity golden values

Golden validation diffs the database this tool exports from a `.snap` against reference numbers
captured from Unity's own Memory Profiler for the **same** snapshot. A pass means the export
agrees with what a developer sees in the profiler; a failure names the exact metric and the
expected/actual values.

**Full reference (schema, every compared metric, tolerances, failure formats):**
[`docs/golden-validation.md`](../../../docs/golden-validation.md). Read it before changing what
validation compares. This skill is the operational quick-path.

**Paths below are relative to the repo root** (the directory containing `MemorySnapshotDataTools.sln`).

## When to use

- Validate / verify an export, or confirm the tool matches Unity's Memory Profiler.
- Produce a golden file in Unity, or interpret a `*_golden.json` / `*_validation_result.json`.
- Debug a validation failure, or change what the comparison covers (also touch the Unity extractor).

## The three steps

The golden file and the database **must come from the same `.snap`** or everything mismatches.

### 1. Extract golden values in Unity (produces `{name}_golden.json`)

The extractor is a Unity Editor package — `com.unity.memory-snapshot-data-tools` in
[`UnityPackage/`](../../../UnityPackage) (Unity 2022.3+, `com.unity.memoryprofiler` 1.1.12), **not**
the .NET CLI. Import it into the target Unity project via a local `file:` path in that project's
`Packages/manifest.json`, then run **Tools → Memory Snapshot Validation → Extract Golden Values**
and pick a `.snap`. It writes `{name}_golden.json` next to the snapshot. It pulls the Summary-page
numbers from the Memory Profiler's own model builders, so golden == what the profiler UI shows.

### 2. Export the same snapshot

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- export <name>.snap <name>.duckdb --validate minimal
```

DuckDB recommended (`.db` + `--destination sqlite` also works). Needs the `native_objects`,
`native_roots`, and `summary_metrics` tables — a current-schema export has them.

### 3. Run `validate`

```bash
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- validate <name>_golden.json <name>.duckdb [--out result.json]
```

- Writes `{name}_golden_validation_result.json` next to the golden file unless `--out` is set, and
  prints the result to stdout.
- **Exit codes:** `0` = passed, `1` = metric mismatch(es), `3` = error (bad input / unparseable
  golden / unsupported DB extension). The DB is schema-gated first; an older **major** schema is
  rejected — re-export rather than `upgrade` (upgrade only covers minor analysis-view changes).

## What it compares (summary — details in the doc)

- **Native types:** `AssetBundle` (from `native_objects`) and `SerializedFile` (from `native_roots`,
  `area_name LIKE '%serializedfile%'`) — Count and AllocatedBytes **exact**, ResidentBytes within tolerance.
- **PMR:** sum of PersistentManager Remapper roots — Allocated exact, Resident within tolerance.
- **Summary page:** Totals + `AllocatedMemoryDistribution` + `ManagedHeapUtilization` rows from the
  `summary_metrics` table.

**Tolerances:** counts & allocated bytes must be **exact**; resident = `max(64 KB, 1%)`; summary
committed = `max(64 KB, 1%)`, except **estimated** rows (Graphics, Untracked — golden
`ResidentAvailable=false`) which get `max(1 MB, 5%)`. Summary comparison is **skipped** when the
golden file has no Summary rows (older/partial golden files still validate on native metrics).

## Reading a failure

Each `Failures[]` entry is `\<metric\>: expected=…, actual=…`, e.g.
`SerializedFile.Count: expected=34, actual=33` or
`Summary[AllocatedMemoryDistribution].Native.Committed: expected=…, actual=…`. A
`… row missing from export` means the export lacks a Summary row the golden file has → re-export
with the current tool. Everything mismatching → golden and DB came from different snapshots.

## Changing what validation compares

Touch **both** sides so they don't drift:

- **Tool:** SQL constants in `Core/Validation/GoldenValidationQueries.cs`, comparison/tolerances in
  `Core/Validation/GoldenValidationRunner.cs`, models in `Core/Validation/GoldenValidationModels.cs`.
- **Unity extractor:** `UnityPackage/com.unity.memory-snapshot-data-tools/Editor/GoldenValueExtractor.cs`
  and shared names in `…/Editor/MemorySnapshotValidationHelpers.cs`.
- Validation SQL must stay constant-only (no interpolated external values) per
  [`docs/sql-safety.md`](../../../docs/sql-safety.md).
- Add/extend tests in [`Tests/GoldenValidationRunnerTests.cs`](../../../Tests/GoldenValidationRunnerTests.cs)
  (they build a SQLite DB + golden JSON in temp and assert pass/fail). Test helpers must be `public`.
- Run `dotnet test MemorySnapshotDataTools.sln` and update [`docs/golden-validation.md`](../../../docs/golden-validation.md).

---
name: add-cli-command
description: Add a new subcommand to the Memory Snapshot Data Tool CLI (the .NET 10 `MemorySnapshotDataTools` exe), wiring it through CliOptions → CommandLineBuilder → Program → a Core runner, then documenting it in README.md. Use when asked to add, create, or expose a new CLI command/subcommand (alongside export, batch-export, report, multi-report, validate, summary, upgrade), or a new flag/argument on an existing one.
---

# Add a new CLI command

The CLI is built with `System.CommandLine`. A subcommand is wired through **three files in
`Cli/`** plus a **Core runner** that holds the logic, and is then **documented in `README.md`**.
Match the existing commands (`export`, `batch-export`, `report`, `multi-report`, `validate`,
`summary`, `upgrade`) — don't invent a new shape.

**Paths below are relative to the repo root** (the directory containing `MemorySnapshotDataTools.sln`).

## When to use

- Adding a brand-new subcommand (e.g. `diff`, `prune`, `export-csv`).
- Adding a new argument or `--option` to an existing subcommand.
- Exposing an existing Core capability through the CLI.

For SQL-touching logic inside the new command, also follow [`CLAUDE.md`](../../../CLAUDE.md) and
[`docs/sql-safety.md`](../../../docs/sql-safety.md) — SQL safety is a first-class rule here.

## The files you touch

| File | What you add |
|------|--------------|
| [`Cli/CliOptions.cs`](../../../Cli/CliOptions.cs) | A `CommandKind` enum value + any new option/arg properties on `CliOptions`. |
| [`Cli/CommandLineBuilder.cs`](../../../Cli/CommandLineBuilder.cs) | The `Command`, its `Argument<>`/`Option<>`s, the `SetAction` handler, `root.Add(...)`, and a new `Func<CliOptions,int>` parameter on `Build`. |
| [`Cli/Program.cs`](../../../Cli/Program.cs) | A `RunXxx(CliOptions)` handler that calls the Core runner, plus threading it through the `Build(...)` call in `Main`. |
| `Core/...` (a runner) | The actual logic — a `XxxRunner` + a `XxxRunOptions`, mirroring `Core/Report/SummaryReportRunner.cs`, `Core/Export/ExportRunner.cs`, etc. |
| [`Tests/`](../../../Tests) | Tests for the **Core runner / calculator** (there are no `CommandLineBuilder` tests; test the logic, not the parser). |
| [`README.md`](../../../README.md) | **Required** — a usage subsection + example for the new command. See [Document in README.md](#document-in-readmemd). |

## Step by step

### 1. `Cli/CliOptions.cs`

- Add a value to the `CommandKind` enum.
- Add a property to `CliOptions` for each new argument/option. Reuse existing ones where they fit
  (`Verbose`, `ReportDbPath`, `Destination`, …) before adding new fields.

### 2. `Cli/CommandLineBuilder.cs`

Inside `Build(...)`, following the existing blocks:

```csharp
// ---- mycommand ----
var myCmd = new Command("mycommand", "One-line description shown in --help.");
var inputArg = new Argument<string>("input")
{
    Description = "Path to ...",
    Arity = ArgumentArity.ExactlyOne,
};
myCmd.Add(inputArg);

var someOpt = new Option<string>("--mode")
{
    Description = "...: a, b, or c.",
    DefaultValueFactory = _ => "a",
};
someOpt.AcceptOnlyFromAmong("a", "b", "c"); // validate enum-like options at parse time
myCmd.Add(someOpt);

myCmd.SetAction((ParseResult parseResult) =>
{
    var inputPath = ExpandPath(parseResult.GetValue(inputArg)!); // ALWAYS ExpandPath path args
    if (!File.Exists(inputPath))                                  // validate existence, return 1
    {
        Console.Error.WriteLine($"Input file not found: {inputPath}");
        return 1;
    }
    var options = new CliOptions
    {
        Command = CommandKind.MyCommand,
        // ... map parsed values onto CliOptions ...
    };
    return runMyCommand(options);
});
```

Then:
- Register it: `root.Add(myCmd);` near the bottom of `Build`.
- Add a `Func<CliOptions, int> runMyCommand` parameter to the `Build(...)` signature (the params are
  positional — keep the order consistent with `Program.Main`).

### 3. `Cli/Program.cs`

- Add a `RunMyCommand(CliOptions options)` static handler that delegates to your Core runner.
  - If the command **reads an exported database**, call `SchemaGate.Check(path)` first (see
    `RunReport`/`RunSummary`).
  - If it does cancellable work, use `CreateCancellationSource()` and catch
    `OperationCanceledException` → return `2` (see `RunExport`).
- Add `RunMyCommand` to the `CommandLineBuilder.Build(...)` call in `Main` in the matching position.

### 4. Core runner (the logic)

Put real work in `Core/...`, not in `Cli/`. Mirror an existing runner
(`SummaryReportRunner`, `ExportRunner`): a `XxxRunOptions` record + a `static int Run(...)` that
returns an exit code and reports via `IProgressReporter`. If it builds SQL, parameterize values and
validate identifiers per [`docs/sql-safety.md`](../../../docs/sql-safety.md); open the DB
**read-only** if it only reads (`Mode=ReadOnly` / `ACCESS_MODE=READ_ONLY`).

### 5. Tests

Add tests under [`Tests/`](../../../Tests) targeting the Core runner / calculator (e.g.
`BatchExportRunnerTests.cs`, `SummaryMetricsCalculatorTests.cs`). Helpers you assert on must be
**`public`** — `InternalsVisibleTo` is a no-op here, so Core internals are not visible to Tests.

### 6. Document in README.md

**This step is required — a new command is not done until the README documents it.** In
[`README.md`](../../../README.md):

- Add a `### <verb the command does>` subsection under **How to use**, after the existing
  command subsections, with the invocation form and a worked example, matching the style of the
  `export` and `report` sections:

  ````markdown
  ### <What the command does>

  ```bash
  dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- mycommand <args> [options]
  ```

  - **`--option`:** what it does (default: …).

  **Example:**

  ```bash
  dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- mycommand ./in.duckdb --mode b
  ```
  ````

- If the command changes the headline behavior, add a bullet to the **What it does** list at the
  top, and update **Output**/**Schema** if it writes new tables/files.
- Consider also updating, when relevant: the **Direct CLI invocation** list in
  [`run-memory-snapshot-data-tool/SKILL.md`](../run-memory-snapshot-data-tool/SKILL.md), and
  `docs/intro.md` / `docs/runbook.md`.

### 7. Build, test, and verify it runs

```bash
dotnet build MemorySnapshotDataTools.sln
dotnet test MemorySnapshotDataTools.sln
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- --help          # command listed?
dotnet run --project Cli/MemorySnapshotDataTools.Cli.csproj -- mycommand --help # args/options correct?
```

To exercise the full pipeline (build → run → screenshot), use the `run-memory-snapshot-data-tool`
skill's driver.

## Conventions & gotchas

- **Always `ExpandPath(...)` path arguments/options** before use — it expands `~`, env vars, and
  resolves to a full path. Every existing handler does this.
- **Validate inputs in `SetAction` and return `1`** for bad args / missing files, before
  constructing `CliOptions`.
- **Exit codes are meaningful and asserted on:** `0` success, `1` bad args / file not found,
  `2` cancelled (Ctrl-C), `3` validation/error. Keep yours consistent.
- **Enum-like string options** → `option.AcceptOnlyFromAmong("a","b","c")` so bad values are
  rejected at parse time (see `--validate`, `--destination`).
- **`Build`'s handlers are positional `Func<CliOptions,int>` parameters.** Add yours in the same
  position in both the `Build` signature and the `Main` call site, or the wrong handler runs.
- **Reading an exported DB?** Call `SchemaGate.Check(path)` first so a stale/newer schema fails
  with a clear message instead of a confusing query error.
- **SQL safety is non-negotiable** — never interpolate external values into SQL; bind parameters and
  validate identifiers (`CLAUDE.md`, `docs/sql-safety.md`).

## Checklist

- [ ] `CommandKind` value + `CliOptions` properties added (`Cli/CliOptions.cs`).
- [ ] Command, args/options, `SetAction`, `root.Add`, and new `Build` param added (`Cli/CommandLineBuilder.cs`).
- [ ] `RunXxx` handler added and threaded through `Main` (`Cli/Program.cs`).
- [ ] Logic lives in a Core `XxxRunner` (+ `XxxRunOptions`); SQL is parameterized / read-only.
- [ ] Tests added for the Core logic (public helpers).
- [ ] **`README.md` documents the command** (usage + example), other docs/skill updated if relevant.
- [ ] `dotnet build` + `dotnet test` pass; `-- --help` and `-- mycommand --help` look right.

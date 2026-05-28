using System.CommandLine;
using MemorySnapshotDataTools;

namespace MemorySnapshotDataTools.Cli;

/// <summary>
/// Builds the root command and subcommands (export, report) using System.CommandLine.
/// </summary>
internal static class CommandLineBuilder
{
    public static RootCommand Build(Func<CliOptions, int> runExport, Func<CliOptions, int> runReport, Func<CliOptions, int> runGephi)
    {
        var root = new RootCommand("Export Unity memory snapshots to DuckDB or SQLite and generate HTML reports.");

        // ---- export ----
        var exportCmd = new Command("export", "Export a .snap file to a DuckDB or SQLite database.");
        var snapshotArg = new Argument<string>("snapshot")
        {
            Description = "Path to the Unity memory snapshot (.snap) file.",
            Arity = ArgumentArity.ExactlyOne,
        };
        var outputArg = new Argument<string>("output")
        {
            Description = "Path to the output database (.duckdb or .db).",
            Arity = ArgumentArity.ExactlyOne,
        };
        exportCmd.Add(snapshotArg);
        exportCmd.Add(outputArg);

        var batchSizeOpt = new Option<int>("--batch-size")
        {
            Description = "Rows per produced batch.",
            DefaultValueFactory = _ => 2048,
        };
        var queueCapacityOpt = new Option<int>("--queue-capacity")
        {
            Description = "Max queued batches.",
            DefaultValueFactory = _ => 256,
        };
        var validateOpt = new Option<string>("--validate")
        {
            Description = "Validation mode: none, minimal, or full.",
            DefaultValueFactory = _ => "minimal",
        };
        validateOpt.AcceptOnlyFromAmong("none", "minimal", "full");
        var destinationOpt = new Option<string>("--destination")
        {
            Description = "Export backend: duckdb or sqlite.",
            DefaultValueFactory = _ => "duckdb",
        };
        destinationOpt.AcceptOnlyFromAmong("duckdb", "sqlite");
        var verboseOpt = new Option<bool>("--verbose")
        {
            Description = "Print progress updates.",
        };

        exportCmd.Add(batchSizeOpt);
        exportCmd.Add(queueCapacityOpt);
        exportCmd.Add(validateOpt);
        exportCmd.Add(destinationOpt);
        exportCmd.Add(verboseOpt);

        exportCmd.SetAction((ParseResult parseResult) =>
        {
            var snapshotPath = ExpandPath(parseResult.GetValue(snapshotArg)!);
            var outputDbPath = ExpandPath(parseResult.GetValue(outputArg)!);
            if (!File.Exists(snapshotPath))
            {
                Console.Error.WriteLine($"Snapshot file not found: {snapshotPath}");
                return 1;
            }
            var options = new CliOptions
            {
                Command = CommandKind.Export,
                SnapshotPath = snapshotPath,
                OutputDbPath = outputDbPath,
                BatchSize = parseResult.GetValue(batchSizeOpt),
                QueueCapacity = parseResult.GetValue(queueCapacityOpt),
                Validate = ParseValidationMode(parseResult.GetValue(validateOpt)!),
                Destination = parseResult.GetValue(destinationOpt)!.ToLowerInvariant() == "sqlite" ? DestinationKind.Sqlite : DestinationKind.DuckDb,
                Verbose = parseResult.GetValue(verboseOpt),
            };
            return runExport(options);
        });

        // ---- report ----
        var reportCmd = new Command("report", "Generate an HTML report from an exported database.");
        var databaseArg = new Argument<string>("database")
        {
            Description = "Path to the exported database (.duckdb or .db).",
            Arity = ArgumentArity.ExactlyOne,
        };
        reportCmd.Add(databaseArg);

        var outOpt = new Option<string?>("--out")
        {
            Description = "Output HTML file path (default: temp file + open in browser).",
        };
        var titleOpt = new Option<string>("--title")
        {
            Description = "Report title.",
            DefaultValueFactory = _ => "Memory Snapshot Report",
        };
        var reportVerboseOpt = new Option<bool>("--verbose")
        {
            Description = "Print progress and timings.",
        };

        reportCmd.Add(outOpt);
        reportCmd.Add(titleOpt);
        reportCmd.Add(reportVerboseOpt);

        reportCmd.SetAction((ParseResult parseResult) =>
        {
            var reportDbPath = ExpandPath(parseResult.GetValue(databaseArg)!);
            if (!File.Exists(reportDbPath))
            {
                Console.Error.WriteLine($"Database file not found: {reportDbPath}");
                return 1;
            }
            var outPath = parseResult.GetValue(outOpt);
            var options = new CliOptions
            {
                Command = CommandKind.Report,
                ReportDbPath = reportDbPath,
                ReportOutputPath = string.IsNullOrWhiteSpace(outPath) ? null : ExpandPath(outPath),
                ReportTitle = parseResult.GetValue(titleOpt)!,
                Verbose = parseResult.GetValue(reportVerboseOpt),
            };
            return runReport(options);
        });

        // ---- gephi ----
        var gephiCmd = new Command("gephi", "Export edge list for Gephi (native, managed, or mixed object graph).");
        var gephiDatabaseArg = new Argument<string>("database")
        {
            Description = "Path to the exported snapshot database (.duckdb or .db).",
            Arity = ArgumentArity.ExactlyOne,
        };
        gephiCmd.Add(gephiDatabaseArg);

        var gephiOutOpt = new Option<string>("--out")
        {
            Description = "Output path for the edges CSV (required).",
        };
        var gephiNodesOpt = new Option<string?>("--nodes")
        {
            Description = "Optional path for the nodes CSV; if omitted, only the edges file is written.",
        };
        var gephiModeOpt = new Option<string>("--mode")
        {
            Description = "Export mode: native (default), managed, or mixed. Only native is implemented.",
            DefaultValueFactory = _ => "native",
        };
        gephiModeOpt.AcceptOnlyFromAmong("native", "managed", "mixed");
        var gephiVerboseOpt = new Option<bool>("--verbose")
        {
            Description = "Print progress updates.",
        };

        gephiCmd.Add(gephiOutOpt);
        gephiCmd.Add(gephiNodesOpt);
        gephiCmd.Add(gephiModeOpt);
        gephiCmd.Add(gephiVerboseOpt);

        gephiCmd.SetAction((ParseResult parseResult) =>
        {
            var dbPath = ExpandPath(parseResult.GetValue(gephiDatabaseArg)!);
            if (!File.Exists(dbPath))
            {
                Console.Error.WriteLine($"Database file not found: {dbPath}");
                return 1;
            }
            var outPath = parseResult.GetValue(gephiOutOpt);
            if (string.IsNullOrWhiteSpace(outPath))
            {
                Console.Error.WriteLine("The --out option is required for the gephi command.");
                return 1;
            }
            var options = new CliOptions
            {
                Command = CommandKind.Gephi,
                GephiDbPath = dbPath,
                GephiOutPath = ExpandPath(outPath),
                GephiNodesPath = parseResult.GetValue(gephiNodesOpt) is { } n && !string.IsNullOrWhiteSpace(n) ? ExpandPath(n) : null,
                GephiMode = parseResult.GetValue(gephiModeOpt)!,
                Verbose = parseResult.GetValue(gephiVerboseOpt),
            };
            return runGephi(options);
        });

        root.Add(exportCmd);
        root.Add(reportCmd);
        root.Add(gephiCmd);
        return root;
    }

    private static ValidationMode ParseValidationMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "none" => ValidationMode.None,
            "minimal" => ValidationMode.Minimal,
            "full" => ValidationMode.Full,
            _ => ValidationMode.Minimal,
        };
    }

    private static string ExpandPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var expanded = Environment.ExpandEnvironmentVariables(value);
        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var suffix = expanded.Length > 2 ? expanded[2..] : string.Empty;
            expanded = Path.Combine(home, suffix);
        }
        return Path.GetFullPath(expanded);
    }
}

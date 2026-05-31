using System.CommandLine;
using MemorySnapshotDataTools;

namespace MemorySnapshotDataTools.Cli;

/// <summary>
/// Builds the root command and subcommands (export, report) using System.CommandLine.
/// </summary>
internal static class CommandLineBuilder
{
    public static RootCommand Build(
        Func<CliOptions, int> runExport,
        Func<CliOptions, int> runBatchExport,
        Func<CliOptions, int> runReport,
        Func<CliOptions, int> runMultiReport,
        Func<CliOptions, int> runValidateGolden,
        Func<CliOptions, int> runSummary)
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

        AddExportOptions(exportCmd, batchSizeOpt, queueCapacityOpt, validateOpt, destinationOpt, verboseOpt);

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

        // ---- batch-export ----
        var batchExportCmd = new Command(
            "batch-export",
            "Export every .snap file in a directory to a .duckdb or .db file with the same basename.");
        var batchDirectoryArg = new Argument<string>("directory")
        {
            Description = "Directory containing .snap files (top level only).",
            Arity = ArgumentArity.ExactlyOne,
        };
        batchExportCmd.Add(batchDirectoryArg);

        var batchFilterOpt = new Option<string?>("--filter")
        {
            Description = "Case-insensitive substring filter on snapshot filenames (e.g. MyGame).",
        };
        var skipExistingOpt = new Option<bool>("--skip-existing")
        {
            Description = "Skip when the output database exists and is newer than the .snap file.",
        };
        var continueOnErrorOpt = new Option<bool>("--continue-on-error")
        {
            Description = "Continue exporting after a single-file failure.",
            DefaultValueFactory = _ => true,
        };

        var batchBatchSizeOpt = new Option<int>("--batch-size")
        {
            Description = "Rows per produced batch.",
            DefaultValueFactory = _ => 2048,
        };
        var batchQueueCapacityOpt = new Option<int>("--queue-capacity")
        {
            Description = "Max queued batches.",
            DefaultValueFactory = _ => 256,
        };
        var batchValidateOpt = new Option<string>("--validate")
        {
            Description = "Validation mode: none, minimal, or full.",
            DefaultValueFactory = _ => "minimal",
        };
        batchValidateOpt.AcceptOnlyFromAmong("none", "minimal", "full");
        var batchDestinationOpt = new Option<string>("--destination")
        {
            Description = "Export backend: duckdb or sqlite.",
            DefaultValueFactory = _ => "duckdb",
        };
        batchDestinationOpt.AcceptOnlyFromAmong("duckdb", "sqlite");
        var batchVerboseOpt = new Option<bool>("--verbose")
        {
            Description = "Print progress updates.",
        };

        batchExportCmd.Add(batchFilterOpt);
        batchExportCmd.Add(skipExistingOpt);
        batchExportCmd.Add(continueOnErrorOpt);
        AddExportOptions(batchExportCmd, batchBatchSizeOpt, batchQueueCapacityOpt, batchValidateOpt, batchDestinationOpt, batchVerboseOpt);

        batchExportCmd.SetAction((ParseResult parseResult) =>
        {
            var directory = ExpandPath(parseResult.GetValue(batchDirectoryArg)!);
            if (!Directory.Exists(directory))
            {
                Console.Error.WriteLine($"Directory not found: {directory}");
                return 1;
            }

            var options = new CliOptions
            {
                Command = CommandKind.BatchExport,
                BatchExportDirectory = directory,
                BatchExportFilter = parseResult.GetValue(batchFilterOpt),
                SkipExisting = parseResult.GetValue(skipExistingOpt),
                ContinueOnError = parseResult.GetValue(continueOnErrorOpt),
                BatchSize = parseResult.GetValue(batchBatchSizeOpt),
                QueueCapacity = parseResult.GetValue(batchQueueCapacityOpt),
                Validate = ParseValidationMode(parseResult.GetValue(batchValidateOpt)!),
                Destination = parseResult.GetValue(batchDestinationOpt)!.ToLowerInvariant() == "sqlite"
                    ? DestinationKind.Sqlite
                    : DestinationKind.DuckDb,
                Verbose = parseResult.GetValue(batchVerboseOpt),
            };
            return runBatchExport(options);
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

        // ---- multi-report ----
        var multiReportCmd = new Command("multi-report", "Generate an HTML report across multiple exported databases in a directory.");
        var directoryArg = new Argument<string>("directory")
        {
            Description = "Directory containing .duckdb or .db snapshot databases.",
            Arity = ArgumentArity.ExactlyOne,
        };
        multiReportCmd.Add(directoryArg);

        var filterOpt = new Option<string?>("--filter")
        {
            Description = "Case-insensitive substring filter on database filenames (e.g. MyGame).",
        };
        var multiOutOpt = new Option<string?>("--out")
        {
            Description = "Output HTML file path (default: temp file + open in browser).",
        };
        var multiTitleOpt = new Option<string>("--title")
        {
            Description = "Report title.",
            DefaultValueFactory = _ => "Multi-Snapshot Memory Report",
        };
        var multiVerboseOpt = new Option<bool>("--verbose")
        {
            Description = "Print progress and timings.",
        };

        multiReportCmd.Add(filterOpt);
        multiReportCmd.Add(multiOutOpt);
        multiReportCmd.Add(multiTitleOpt);
        multiReportCmd.Add(multiVerboseOpt);

        multiReportCmd.SetAction((ParseResult parseResult) =>
        {
            var directory = ExpandPath(parseResult.GetValue(directoryArg)!);
            if (!Directory.Exists(directory))
            {
                Console.Error.WriteLine($"Directory not found: {directory}");
                return 1;
            }

            var outPath = parseResult.GetValue(multiOutOpt);
            var options = new CliOptions
            {
                Command = CommandKind.MultiReport,
                MultiReportDirectory = directory,
                MultiReportFilter = parseResult.GetValue(filterOpt),
                ReportOutputPath = string.IsNullOrWhiteSpace(outPath) ? null : ExpandPath(outPath!),
                ReportTitle = parseResult.GetValue(multiTitleOpt)!,
                Verbose = parseResult.GetValue(multiVerboseOpt),
            };
            return runMultiReport(options);
        });

        // ---- validate ----
        var validateCmd = new Command(
            "validate",
            "Compare an exported database against a Unity golden JSON file.");
        var goldenArg = new Argument<string>("golden")
        {
            Description = "Path to *_golden.json from Unity GoldenValueExtractor.",
            Arity = ArgumentArity.ExactlyOne,
        };
        var validateDatabaseArg = new Argument<string>("database")
        {
            Description = "Path to the exported .duckdb or .db file.",
            Arity = ArgumentArity.ExactlyOne,
        };
        validateCmd.Add(goldenArg);
        validateCmd.Add(validateDatabaseArg);

        var validateOutOpt = new Option<string?>("--out")
        {
            Description = "Output validation result JSON path (default: next to golden file).",
        };
        validateCmd.Add(validateOutOpt);

        validateCmd.SetAction((ParseResult parseResult) =>
        {
            var goldenPath = ExpandPath(parseResult.GetValue(goldenArg)!);
            var databasePath = ExpandPath(parseResult.GetValue(validateDatabaseArg)!);
            var options = new CliOptions
            {
                Command = CommandKind.ValidateGolden,
                GoldenPath = goldenPath,
                ReportDbPath = databasePath,
                ValidationOutputPath = parseResult.GetValue(validateOutOpt),
            };
            return runValidateGolden(options);
        });

        // ---- summary ----
        var summaryCmd = new Command(
            "summary",
            "Print a high-level memory-usage summary for a snapshot or exported database (no database is generated).");
        var summaryInputArg = new Argument<string>("input")
        {
            Description = "Path to a .snap snapshot or an exported .duckdb/.db database.",
            Arity = ArgumentArity.ExactlyOne,
        };
        summaryCmd.Add(summaryInputArg);

        var summaryVerboseOpt = new Option<bool>("--verbose")
        {
            Description = "Print progress while decoding a snapshot.",
        };
        summaryCmd.Add(summaryVerboseOpt);

        summaryCmd.SetAction((ParseResult parseResult) =>
        {
            var inputPath = ExpandPath(parseResult.GetValue(summaryInputArg)!);
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Input file not found: {inputPath}");
                return 1;
            }

            var options = new CliOptions
            {
                Command = CommandKind.Summary,
                SummaryInputPath = inputPath,
                Verbose = parseResult.GetValue(summaryVerboseOpt),
            };
            return runSummary(options);
        });

        root.Add(exportCmd);
        root.Add(batchExportCmd);
        root.Add(reportCmd);
        root.Add(multiReportCmd);
        root.Add(validateCmd);
        root.Add(summaryCmd);
        return root;
    }

    private static void AddExportOptions(
        Command command,
        Option<int> batchSizeOpt,
        Option<int> queueCapacityOpt,
        Option<string> validateOpt,
        Option<string> destinationOpt,
        Option<bool> verboseOpt)
    {
        command.Add(batchSizeOpt);
        command.Add(queueCapacityOpt);
        command.Add(validateOpt);
        command.Add(destinationOpt);
        command.Add(verboseOpt);
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

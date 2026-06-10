using MemorySnapshotDataTools;
using MemorySnapshotDataTools.Export;
using MemorySnapshotDataTools.ExportDestination;
using MemorySnapshotDataTools.Report;
using MemorySnapshotDataTools.Report.MultiSnapshotReport;
using MemorySnapshotDataTools.Validation;

namespace MemorySnapshotDataTools.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var root = CommandLineBuilder.Build(RunExport, RunBatchExport, RunReport, RunMultiReport, RunValidateGolden, RunSummary, RunUpgrade);
        return root.Parse(args).Invoke();
    }

    private static int RunExport(CliOptions options)
    {
        var progress = new ConsoleProgress(options.Verbose);
        using var cts = CreateCancellationSource();

        try
        {
            return ExportRunner.Run(
                options.SnapshotPath,
                options.OutputDbPath,
                new ExportRunOptions
                {
                    BatchSize = options.BatchSize,
                    QueueCapacity = options.QueueCapacity,
                    Validate = options.Validate,
                },
                options.Destination,
                progress,
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Export cancelled.");
            return 2;
        }
    }

    private static int RunBatchExport(CliOptions options)
    {
        var progress = new ConsoleProgress(options.Verbose);
        using var cts = CreateCancellationSource();

        try
        {
            return BatchExportRunner.Run(
                new BatchExportRunOptions
                {
                    Directory = options.BatchExportDirectory,
                    NameFilter = options.BatchExportFilter,
                    Destination = options.Destination,
                    BatchSize = options.BatchSize,
                    QueueCapacity = options.QueueCapacity,
                    Validate = options.Validate,
                    SkipExisting = options.SkipExisting,
                    ContinueOnError = options.ContinueOnError,
                },
                progress,
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Batch export cancelled.");
            return 2;
        }
    }

    private static int RunReport(CliOptions options)
    {
        SchemaGate.Check(options.ReportDbPath);
        var reportOptions = new ReportRunOptions
        {
            ReportDbPath = options.ReportDbPath,
            ReportOutputPath = options.ReportOutputPath,
            ReportTitle = options.ReportTitle,
        };
        var progress = new ConsoleProgress(options.Verbose);
        return ReportRunner.Run(reportOptions, progress);
    }

    private static int RunMultiReport(CliOptions options)
    {
        var multiOptions = new MultiSnapshotReportRunOptions
        {
            Directory = options.MultiReportDirectory,
            NameFilter = options.MultiReportFilter,
            ReportOutputPath = options.ReportOutputPath,
            ReportTitle = options.ReportTitle,
            GenerateReports = !options.MultiReportNoReports,
        };
        var progress = new ConsoleProgress(options.Verbose);
        return MultiSnapshotReportRunner.Run(multiOptions, progress);
    }

    private static int RunValidateGolden(CliOptions options)
    {
        SchemaGate.Check(options.ReportDbPath);
        try
        {
            return GoldenValidationRunner.ValidateAndWriteResult(
                options.GoldenPath,
                options.ReportDbPath,
                options.ValidationOutputPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Golden validation failed.");
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
    }

    private static int RunSummary(CliOptions options)
    {
        // Summary accepts either a .snap or an exported database; only databases have a schema to check.
        SchemaGate.Check(options.SummaryInputPath);
        var progress = new ConsoleProgress(options.Verbose);
        using var cts = CreateCancellationSource();

        try
        {
            return SummaryReportRunner.Run(
                new SummaryRunOptions
                {
                    InputPath = options.SummaryInputPath,
                    Verbose = options.Verbose,
                },
                progress,
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Summary cancelled.");
            return 2;
        }
    }

    private static int RunUpgrade(CliOptions options)
    {
        try
        {
            var before = DatabaseMaintenance.Inspect(options.UpgradeDbPath);
            var current = $"v{DatabaseSchemaInfo.SchemaMajor}.{DatabaseSchemaInfo.SchemaMinor}";

            switch (before.Action)
            {
                case SchemaAction.None:
                    Console.WriteLine($"Database is already at the current schema {current}. Nothing to do.");
                    return 0;

                case SchemaAction.ToolOutdated:
                    Console.Error.WriteLine(
                        $"Database schema v{before.Major}.{before.Minor} is newer than this build ({current}). " +
                        $"Update {DatabaseSchemaInfo.ToolName} instead of downgrading.");
                    return 1;

                case SchemaAction.ReExport:
                    Console.Error.WriteLine(
                        $"Database major version (v{before.Major}) is behind v{DatabaseSchemaInfo.SchemaMajor}; an in-place upgrade is not possible. " +
                        "Re-export from the original snapshot:");
                    Console.Error.WriteLine($"  {before.ReExportCommand ?? $"{DatabaseSchemaInfo.ToolName} export <snapshot.snap> \"{options.UpgradeDbPath}\""}");
                    return 1;

                case SchemaAction.UpgradeInPlace:
                    DatabaseMaintenance.UpgradeInPlace(options.UpgradeDbPath);
                    Console.WriteLine($"Upgraded database schema from v{before.Major}.{before.Minor} to {current}.");
                    var applied = DatabaseSchemaInfo.ChangesSince(before.Major, before.Minor);
                    if (applied.Count > 0)
                    {
                        Console.WriteLine("Applied (views/indexes re-created):");
                        foreach (var change in applied)
                            Console.WriteLine($"  • {change}");
                    }
                    return 0;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Schema upgrade failed.");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static CancellationTokenSource CreateCancellationSource()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return cts;
    }
}

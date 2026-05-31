using MemorySnapshotDataTools;
using MemorySnapshotDataTools.Export;
using MemorySnapshotDataTools.Report;
using MemorySnapshotDataTools.Report.MultiSnapshotReport;
using MemorySnapshotDataTools.Validation;

namespace MemorySnapshotDataTools.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var root = CommandLineBuilder.Build(RunExport, RunBatchExport, RunReport, RunMultiReport, RunValidateGolden, RunSummary);
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
        };
        var progress = new ConsoleProgress(options.Verbose);
        return MultiSnapshotReportRunner.Run(multiOptions, progress);
    }

    private static int RunValidateGolden(CliOptions options)
    {
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

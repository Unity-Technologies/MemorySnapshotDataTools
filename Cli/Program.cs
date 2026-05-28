using System.Diagnostics;
using MemorySnapshotDataTools;
using MemorySnapshotDataTools.Export;
using MemorySnapshotDataTools.ExportDestination;
using MemorySnapshotDataTools.Gephi;
using MemorySnapshotDataTools.Parser;
using MemorySnapshotDataTools.Report;

namespace MemorySnapshotDataTools.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var root = CommandLineBuilder.Build(RunExport, RunReport, RunGephi);
        return root.Parse(args).Invoke();
    }

    private static int RunExport(CliOptions options)
    {
        var destination = ExportDestinationFactory.Create(options.Destination);
        var progress = new ConsoleProgress(options.Verbose);
        progress.Report($"Backend: {destination.DestinationName}", force: true);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            var sw = Stopwatch.StartNew();

            var exportOptions = new ExportRunOptions
            {
                OutputDbPath = options.OutputDbPath,
                BatchSize = options.BatchSize,
                QueueCapacity = options.QueueCapacity,
                Validate = options.Validate,
            };

            var extractSw = Stopwatch.StartNew();
            var rawData = RunStage("snapshot-extract", progress, () => SnapshotBridge.ExtractRawData(options.SnapshotPath, progress, cts.Token));
            extractSw.Stop();

            var pipelineSw = Stopwatch.StartNew();
            var counts = RunStage("pipeline-write", progress, () => ExportPipeline.Run(exportOptions, rawData, destination, progress, cts.Token));
            pipelineSw.Stop();

            var validationSw = Stopwatch.StartNew();
            RunStage("validation", progress, () => destination.Validate(options.OutputDbPath, rawData, options.Validate));
            validationSw.Stop();

            counts.TotalMs = sw.ElapsedMilliseconds;
            var pipelineRps = pipelineSw.ElapsedMilliseconds > 0
                ? rawData.TotalRows * 1000.0 / pipelineSw.ElapsedMilliseconds
                : 0.0;

            progress.Report(
                $"Done. backend={destination.DestinationName}, native_objects={counts.NativeObjects}, managed_objects={counts.ManagedObjects}, connections={counts.Connections}, native_roots={counts.NativeRoots}, " +
                $"memory_regions={counts.MemoryRegions}, native_allocations={counts.NativeAllocations}, " +
                $"extract_ms={extractSw.ElapsedMilliseconds}, pipeline_ms={pipelineSw.ElapsedMilliseconds}, validation_ms={validationSw.ElapsedMilliseconds}, total_ms={counts.TotalMs}, " +
                $"pipeline_rps={pipelineRps:N0}, backend_insert_ms={counts.BackendInsertMs}, backend_commit_ms={counts.BackendCommitMs}, backend_index_ms={counts.BackendIndexBuildMs}, " +
                $"insert_ms_by_table(native={counts.NativeObjectInsertMs}, managed={counts.ManagedObjectInsertMs}, connections={counts.ConnectionInsertMs}, roots={counts.NativeRootInsertMs}, regions={counts.MemoryRegionInsertMs}, allocations={counts.NativeAllocationInsertMs})");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Export cancelled.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Export failed.");
            if (ex is ExportStageException stageEx)
            {
                Console.Error.WriteLine($"Failure stage: {stageEx.Stage}");
                Console.Error.WriteLine(stageEx.InnerException ?? stageEx);
            }
            else
            {
                Console.Error.WriteLine(ex);
            }
            return 3;
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

    private static int RunGephi(CliOptions options)
    {
        var progress = new ConsoleProgress(options.Verbose);
        try
        {
            GephiExport.RunFromDatabase(
                options.GephiDbPath,
                options.GephiOutPath,
                options.GephiNodesPath,
                options.GephiMode,
                progress);
            progress.Report($"Gephi export complete: edges -> {options.GephiOutPath}" +
                (options.GephiNodesPath != null ? $", nodes -> {options.GephiNodesPath}" : ""), force: true);
            return 0;
        }
        catch (NotSupportedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Gephi export failed.");
            Console.Error.WriteLine(ex);
            return 3;
        }
    }

    private static void RunStage(string stage, ConsoleProgress progress, Action action)
    {
        progress.Report($"[{stage}] start", force: true);
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not ExportStageException)
        {
            throw new ExportStageException(stage, ex);
        }
    }

    private static T RunStage<T>(string stage, ConsoleProgress progress, Func<T> action)
    {
        progress.Report($"[{stage}] start", force: true);
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not ExportStageException)
        {
            throw new ExportStageException(stage, ex);
        }
    }
}

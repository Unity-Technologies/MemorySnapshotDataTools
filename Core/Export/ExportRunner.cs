using System.Diagnostics;
using MemorySnapshotDataTools.ExportDestination;
using MemorySnapshotDataTools.Parser;

namespace MemorySnapshotDataTools.Export;

/// <summary>
/// Runs a single snapshot export: extract, write database, validate.
/// </summary>
public static class ExportRunner
{
    /// <summary>
    /// Exports one <paramref name="snapshotPath"/> to <paramref name="outputDbPath"/>.
    /// </summary>
    /// <param name="snapshotPath">Path to the .snap file.</param>
    /// <param name="outputDbPath">Output database path (.duckdb or .db).</param>
    /// <param name="pipelineOptions">Pipeline batch/validation options.</param>
    /// <param name="destination">DuckDB or SQLite backend.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>Process exit code: 0 success, 2 cancelled, 3 error.</returns>
    public static int Run(
        string snapshotPath,
        string outputDbPath,
        ExportRunOptions pipelineOptions,
        DestinationKind destination,
        IProgressReporter progress,
        CancellationToken token = default)
    {
        var destinationWriter = ExportDestinationFactory.Create(destination);
        progress.Report($"Backend: {destinationWriter.DestinationName}", force: true);

        try
        {
            var sw = Stopwatch.StartNew();

            var exportOptions = new ExportRunOptions
            {
                OutputDbPath = outputDbPath,
                BatchSize = pipelineOptions.BatchSize,
                QueueCapacity = pipelineOptions.QueueCapacity,
                Validate = pipelineOptions.Validate,
            };

            var extractSw = Stopwatch.StartNew();
            var rawData = RunStage("snapshot-extract", progress, () =>
                SnapshotBridge.ExtractRawData(snapshotPath, progress, token));
            extractSw.Stop();
            token.ThrowIfCancellationRequested();

            var pipelineSw = Stopwatch.StartNew();
            var counts = RunStage("pipeline-write", progress, () =>
                ExportPipeline.Run(exportOptions, rawData, destinationWriter, progress, token));
            pipelineSw.Stop();
            token.ThrowIfCancellationRequested();

            RunStage("summary-metrics-write", progress, () =>
                destinationWriter.WriteSummaryMetrics(outputDbPath, rawData.SummaryMetrics));

            var validationSw = Stopwatch.StartNew();
            RunStage("validation", progress, () =>
                destinationWriter.Validate(outputDbPath, rawData, pipelineOptions.Validate));
            validationSw.Stop();

            counts.TotalMs = sw.ElapsedMilliseconds;
            var pipelineRps = pipelineSw.ElapsedMilliseconds > 0
                ? rawData.TotalRows * 1000.0 / pipelineSw.ElapsedMilliseconds
                : 0.0;

            progress.Report(
                $"Done. backend={destinationWriter.DestinationName}, native_objects={counts.NativeObjects}, managed_objects={counts.ManagedObjects}, connections={counts.Connections}, native_roots={counts.NativeRoots}, " +
                $"memory_regions={counts.MemoryRegions}, native_allocations={counts.NativeAllocations}, system_memory_regions={counts.SystemMemoryRegions}, " +
                $"extract_ms={extractSw.ElapsedMilliseconds}, pipeline_ms={pipelineSw.ElapsedMilliseconds}, validation_ms={validationSw.ElapsedMilliseconds}, total_ms={counts.TotalMs}, " +
                $"pipeline_rps={pipelineRps:N0}, backend_insert_ms={counts.BackendInsertMs}, backend_commit_ms={counts.BackendCommitMs}, backend_index_ms={counts.BackendIndexBuildMs}, " +
                $"insert_ms_by_table(native={counts.NativeObjectInsertMs}, managed={counts.ManagedObjectInsertMs}, connections={counts.ConnectionInsertMs}, roots={counts.NativeRootInsertMs}, regions={counts.MemoryRegionInsertMs}, allocations={counts.NativeAllocationInsertMs})");
            return 0;
        }
        catch (OperationCanceledException)
        {
            progress.Report("Export cancelled.", force: true);
            return 2;
        }
        catch (Exception ex)
        {
            progress.Report("Export failed.", force: true);
            if (ex is ExportStageException stageEx)
            {
                progress.Report($"Failure stage: {stageEx.Stage}", force: true);
                progress.Report((stageEx.InnerException ?? stageEx).ToString(), force: true);
            }
            else
            {
                progress.Report(ex.ToString(), force: true);
            }

            return 3;
        }
    }

    private static void RunStage(string stage, IProgressReporter progress, Action action)
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

    private static T RunStage<T>(string stage, IProgressReporter progress, Func<T> action)
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

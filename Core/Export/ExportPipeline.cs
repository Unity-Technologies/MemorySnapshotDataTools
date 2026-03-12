using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using MemorySnapshotDataTools;
using MemorySnapshotDataTools.ExportDestination;

namespace MemorySnapshotDataTools.Export;

/// <summary>
/// Orchestrates parallel export of <see cref="RawSnapshotData"/> to a database: producers materialize batches per table,
/// a single writer consumes from a bounded queue and writes via <see cref="IExportDestinationWriter"/>.
/// Reports progress and respects cancellation.
/// </summary>
public static class ExportPipeline
{
    /// <summary>Minimum interval (ms) between progress reports during materialize+write to avoid flooding the console.</summary>
    private const int ProgressReportIntervalMs = 350;

    /// <summary>Sleep (ms) between monitor loop iterations when waiting on producers or writer.</summary>
    private const int MonitorPollIntervalMs = 125;

    /// <summary>
    /// Runs the full export: starts the destination writer and parallel producers, monitors until completion, then returns counts and timings.
    /// Validates that materialized and written row counts match the raw data.
    /// </summary>
    /// <param name="options">Batch size, queue capacity, output path.</param>
    /// <param name="rawData">Extracted snapshot data to export.</param>
    /// <param name="destination">Writer implementation (DuckDB or SQLite).</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>Row counts and timing statistics.</returns>
    /// <exception cref="InvalidOperationException">If materialized or written row counts do not match.</exception>
    /// <exception cref="OperationCanceledException">When <paramref name="token"/> is cancelled.</exception>
    public static ExportCounts Run(ExportRunOptions options, RawSnapshotData rawData, IExportDestinationWriter destination, IProgressReporter progress, CancellationToken token)
    {
        var counts = new ExportCounts();
        var state = new PipelineState(rawData.TotalRows);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var queue = new BlockingCollection<WriteBatch>(options.QueueCapacity);

        progress.Report($"Starting {destination.DestinationName} writer with {rawData.TotalRows:N0} total rows...", force: true);
        var writerTask = Task.Run(
            () => destination.ConsumeAndWrite(options.OutputDbPath, rawData.SnapshotInfo, queue, state, cts.Token),
            cts.Token);

        var materializeSw = Stopwatch.StartNew();
        var producerTasks = new[]
        {
            Task.Run(() => ProduceNativeRoots(rawData.NativeRoots, queue, state, options.BatchSize, cts.Token), cts.Token),
            Task.Run(() => ProduceMemoryRegions(rawData.MemoryRegions, queue, state, options.BatchSize, cts.Token), cts.Token),
            Task.Run(() => ProduceNativeAllocations(rawData.NativeAllocations, queue, state, options.BatchSize, cts.Token), cts.Token),
            Task.Run(() => ProduceNativeObjects(rawData.NativeObjects, queue, state, options.BatchSize, cts.Token), cts.Token),
            Task.Run(() => ProduceManagedObjects(rawData.ManagedObjects, queue, state, options.BatchSize, cts.Token), cts.Token),
            Task.Run(() => ProduceConnections(rawData.Connections, queue, state, options.BatchSize, cts.Token), cts.Token),
        };

        MonitorOverlap(producerTasks, writerTask, queue, progress, state, options.QueueCapacity, cts);
        materializeSw.Stop();

        var writeSw = Stopwatch.StartNew();
        MonitorWriter(writerTask, progress, state, options.QueueCapacity, cts);
        writeSw.Stop();
        var writeStats = writerTask.GetAwaiter().GetResult();

        counts.NativeObjects = rawData.NativeObjects.Count;
        counts.ManagedObjects = rawData.ManagedObjects.Count;
        counts.Connections = rawData.Connections.Count;
        counts.NativeRoots = rawData.NativeRoots.Count;
        counts.MemoryRegions = rawData.MemoryRegions.Count;
        counts.NativeAllocations = rawData.NativeAllocations.Count;
        counts.MaterializeMs = materializeSw.ElapsedMilliseconds;
        counts.WriteMs = writeSw.ElapsedMilliseconds;
        counts.BackendInsertMs = writeStats.TotalInsertMs;
        counts.BackendCommitMs = writeStats.CommitMs;
        counts.BackendIndexBuildMs = writeStats.IndexBuildMs;
        counts.NativeObjectInsertMs = writeStats.NativeObjectInsertMs;
        counts.ManagedObjectInsertMs = writeStats.ManagedObjectInsertMs;
        counts.ConnectionInsertMs = writeStats.ConnectionInsertMs;
        counts.NativeRootInsertMs = writeStats.NativeRootInsertMs;
        counts.MemoryRegionInsertMs = writeStats.MemoryRegionInsertMs;
        counts.NativeAllocationInsertMs = writeStats.NativeAllocationInsertMs;

        if (state.MaterializedRows != rawData.TotalRows)
            throw new InvalidOperationException($"Materialized rows mismatch. expected={rawData.TotalRows}, actual={state.MaterializedRows}");
        if (state.WrittenRows != rawData.TotalRows + 1)
            throw new InvalidOperationException($"Written rows mismatch. expected={rawData.TotalRows + 1}, actual={state.WrittenRows}");

        return counts;
    }

    private static void MonitorOverlap(
        Task[] producerTasks,
        Task writerTask,
        BlockingCollection<WriteBatch> queue,
        IProgressReporter progress,
        PipelineState state,
        int queueCapacity,
        CancellationTokenSource cts)
    {
        var lastWrite = DateTime.MinValue;
        while (producerTasks.Any(t => !t.IsCompleted))
        {
            ThrowIfFaulted(producerTasks, writerTask);
            var produced = state.MaterializedRows;
            var written = Math.Max(0, state.WrittenRows - 1);
            if (DateTime.UtcNow - lastWrite > TimeSpan.FromMilliseconds(ProgressReportIntervalMs))
            {
                progress.Report(
                    $"parallel materialize+write: produced={produced:N0}/{state.TotalRows:N0}, written={written:N0}/{state.TotalRows:N0}, queued={state.QueuedBatchCount:N0}/{queueCapacity:N0}");
                lastWrite = DateTime.UtcNow;
            }

            Thread.Sleep(MonitorPollIntervalMs);
        }

        Task.WaitAll(producerTasks);
        queue.CompleteAdding();
        progress.Report($"Materialization complete ({state.MaterializedRows:N0}/{state.TotalRows:N0}).", force: true);
    }

    private static void MonitorWriter(Task writerTask, IProgressReporter progress, PipelineState state, int queueCapacity, CancellationTokenSource cts)
    {
        var lastWrite = DateTime.MinValue;
        while (!writerTask.IsCompleted)
        {
            if (writerTask.IsFaulted)
                RethrowTaskException(writerTask, "Writer task failed.");
            if (writerTask.IsCanceled)
                throw new OperationCanceledException();

            if (DateTime.UtcNow - lastWrite > TimeSpan.FromMilliseconds(ProgressReportIntervalMs))
            {
                progress.Report($"writing: written={Math.Max(0, state.WrittenRows - 1):N0}/{state.TotalRows:N0}, queued={state.QueuedBatchCount:N0}/{queueCapacity:N0}");
                lastWrite = DateTime.UtcNow;
            }
            Thread.Sleep(MonitorPollIntervalMs);
        }

        progress.Report($"Write complete ({Math.Max(0, state.WrittenRows - 1):N0}/{state.TotalRows:N0}).", force: true);
    }

    private static void ProduceNativeRoots(List<NativeRootRow> rows, BlockingCollection<WriteBatch> queue, PipelineState state, int batchSize, CancellationToken token)
    {
        ProduceBatches(rows.Count, batchSize, token, start =>
        {
            var end = Math.Min(start + batchSize, rows.Count);
            var buffer = new NativeRootRow[end - start];
            rows.CopyTo(start, buffer, 0, buffer.Length);
            queue.Add(WriteBatch.ForNativeRoots(buffer), token);
            state.IncrementQueuedBatches();
            state.AddMaterialized(buffer.Length);
        });
    }

    private static void ProduceNativeObjects(List<NativeObjectRow> rows, BlockingCollection<WriteBatch> queue, PipelineState state, int batchSize, CancellationToken token)
    {
        ProduceBatches(rows.Count, batchSize, token, start =>
        {
            var end = Math.Min(start + batchSize, rows.Count);
            var buffer = new NativeObjectRow[end - start];
            rows.CopyTo(start, buffer, 0, buffer.Length);
            queue.Add(WriteBatch.ForNativeObjects(buffer), token);
            state.IncrementQueuedBatches();
            state.AddMaterialized(buffer.Length);
        });
    }

    private static void ProduceMemoryRegions(List<MemoryRegionRow> rows, BlockingCollection<WriteBatch> queue, PipelineState state, int batchSize, CancellationToken token)
    {
        ProduceBatches(rows.Count, batchSize, token, start =>
        {
            var end = Math.Min(start + batchSize, rows.Count);
            var buffer = new MemoryRegionRow[end - start];
            rows.CopyTo(start, buffer, 0, buffer.Length);
            queue.Add(WriteBatch.ForMemoryRegions(buffer), token);
            state.IncrementQueuedBatches();
            state.AddMaterialized(buffer.Length);
        });
    }

    private static void ProduceNativeAllocations(List<NativeAllocationRow> rows, BlockingCollection<WriteBatch> queue, PipelineState state, int batchSize, CancellationToken token)
    {
        ProduceBatches(rows.Count, batchSize, token, start =>
        {
            var end = Math.Min(start + batchSize, rows.Count);
            var buffer = new NativeAllocationRow[end - start];
            rows.CopyTo(start, buffer, 0, buffer.Length);
            queue.Add(WriteBatch.ForNativeAllocations(buffer), token);
            state.IncrementQueuedBatches();
            state.AddMaterialized(buffer.Length);
        });
    }

    private static void ProduceManagedObjects(List<ManagedObjectRow> rows, BlockingCollection<WriteBatch> queue, PipelineState state, int batchSize, CancellationToken token)
    {
        ProduceBatches(rows.Count, batchSize, token, start =>
        {
            var end = Math.Min(start + batchSize, rows.Count);
            var buffer = new ManagedObjectRow[end - start];
            rows.CopyTo(start, buffer, 0, buffer.Length);
            queue.Add(WriteBatch.ForManagedObjects(buffer), token);
            state.IncrementQueuedBatches();
            state.AddMaterialized(buffer.Length);
        });
    }

    private static void ProduceConnections(List<ConnectionRow> rows, BlockingCollection<WriteBatch> queue, PipelineState state, int batchSize, CancellationToken token)
    {
        ProduceBatches(rows.Count, batchSize, token, start =>
        {
            var end = Math.Min(start + batchSize, rows.Count);
            var buffer = new ConnectionRow[end - start];
            rows.CopyTo(start, buffer, 0, buffer.Length);
            queue.Add(WriteBatch.ForConnections(buffer), token);
            state.IncrementQueuedBatches();
            state.AddMaterialized(buffer.Length);
        });
    }

    private static void ProduceBatches(int totalCount, int batchSize, CancellationToken token, Action<int> processBatch)
    {
        if (totalCount <= 0)
            return;

        var batchCount = (totalCount + batchSize - 1) / batchSize;
        var starts = new int[batchCount];
        for (var i = 0; i < batchCount; i++)
            starts[i] = i * batchSize;
        Parallel.ForEach(starts, new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
        }, start =>
        {
            token.ThrowIfCancellationRequested();
            processBatch(start);
        });
    }

    private static void ThrowIfFaulted(Task[] producerTasks, Task writerTask)
    {
        foreach (var task in producerTasks)
        {
            if (task.IsFaulted)
                RethrowTaskException(task, "Producer task failed.");
            if (task.IsCanceled)
                throw new OperationCanceledException();
        }

        if (writerTask.IsFaulted)
            RethrowTaskException(writerTask, "Writer task failed.");
        if (writerTask.IsCanceled)
            throw new OperationCanceledException();
    }

    private static void RethrowTaskException(Task task, string fallbackMessage)
    {
        var aggregate = task.Exception;
        if (aggregate == null)
            throw new InvalidOperationException(fallbackMessage);

        var inner = aggregate.InnerException ?? aggregate;
        ExceptionDispatchInfo.Capture(inner).Throw();
        throw new InvalidOperationException(fallbackMessage);
    }
}

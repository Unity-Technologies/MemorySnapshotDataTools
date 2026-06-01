using System.Threading;

namespace MemorySnapshotDataTools;

/// <summary>
/// Kind of batch in the producer/consumer pipeline: each batch carries one table's rows.
/// </summary>
public enum WriteBatchKind
{
    NativeObjects,
    ManagedObjects,
    Connections,
    NativeRoots,
    MemoryRegions,
    NativeAllocations,
    SystemMemoryRegions,
}

/// <summary>
/// A single batch of rows to write, produced by the export pipeline and consumed by
/// <see cref="ExportDestination.IExportDestinationWriter.ConsumeAndWrite"/>.
/// Only the list matching <see cref="Kind"/> is populated.
/// </summary>
public sealed class WriteBatch
{
    /// <summary>Which table this batch belongs to.</summary>
    public WriteBatchKind Kind { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <see cref="WriteBatchKind.NativeObjects"/>.</summary>
    public NativeObjectRow[] NativeObjects { get; init; } = [];

    /// <summary>Populated when <see cref="Kind"/> is <see cref="WriteBatchKind.ManagedObjects"/>.</summary>
    public ManagedObjectRow[] ManagedObjects { get; init; } = [];

    /// <summary>Populated when <see cref="Kind"/> is <see cref="WriteBatchKind.Connections"/>.</summary>
    public ConnectionRow[] Connections { get; init; } = [];

    /// <summary>Populated when <see cref="Kind"/> is <see cref="WriteBatchKind.NativeRoots"/>.</summary>
    public NativeRootRow[] NativeRoots { get; init; } = [];

    /// <summary>Populated when <see cref="Kind"/> is <see cref="WriteBatchKind.MemoryRegions"/>.</summary>
    public MemoryRegionRow[] MemoryRegions { get; init; } = [];

    /// <summary>Populated when <see cref="Kind"/> is <see cref="WriteBatchKind.NativeAllocations"/>.</summary>
    public NativeAllocationRow[] NativeAllocations { get; init; } = [];

    /// <summary>Populated when <see cref="Kind"/> is <see cref="WriteBatchKind.SystemMemoryRegions"/>.</summary>
    public SystemMemoryRegionRow[] SystemMemoryRegions { get; init; } = [];

    /// <summary>Creates a batch of native object rows.</summary>
    public static WriteBatch ForNativeObjects(NativeObjectRow[] rows) => new() { Kind = WriteBatchKind.NativeObjects, NativeObjects = rows };

    /// <summary>Creates a batch of managed object rows.</summary>
    public static WriteBatch ForManagedObjects(ManagedObjectRow[] rows) => new() { Kind = WriteBatchKind.ManagedObjects, ManagedObjects = rows };

    /// <summary>Creates a batch of connection rows.</summary>
    public static WriteBatch ForConnections(ConnectionRow[] rows) => new() { Kind = WriteBatchKind.Connections, Connections = rows };

    /// <summary>Creates a batch of native root rows.</summary>
    public static WriteBatch ForNativeRoots(NativeRootRow[] rows) => new() { Kind = WriteBatchKind.NativeRoots, NativeRoots = rows };

    /// <summary>Creates a batch of memory region rows.</summary>
    public static WriteBatch ForMemoryRegions(MemoryRegionRow[] rows) => new() { Kind = WriteBatchKind.MemoryRegions, MemoryRegions = rows };

    /// <summary>Creates a batch of native allocation rows.</summary>
    public static WriteBatch ForNativeAllocations(NativeAllocationRow[] rows) => new() { Kind = WriteBatchKind.NativeAllocations, NativeAllocations = rows };

    /// <summary>Creates a batch of OS system memory region rows.</summary>
    public static WriteBatch ForSystemMemoryRegions(SystemMemoryRegionRow[] rows) => new() { Kind = WriteBatchKind.SystemMemoryRegions, SystemMemoryRegions = rows };
}

/// <summary>
/// Shared state for the export pipeline: total rows, materialized count, written count, and queued batch count.
/// Updated by producers (materialized, queued) and the writer (written, queued). Used for progress and sanity checks.
/// </summary>
public sealed class PipelineState
{
    /// <summary>
    /// Creates state for a run with the given total row count (for progress).
    /// </summary>
    public PipelineState(long totalRows)
    {
        TotalRows = Math.Max(0, totalRows);
    }

    /// <summary>Total rows to process (sum of all list counts in <see cref="RawSnapshotData"/>).</summary>
    public long TotalRows { get; }

    /// <summary>Rows materialized so far by producers.</summary>
    public long MaterializedRows => Interlocked.Read(ref _materializedRows);

    /// <summary>Rows written so far by the destination writer.</summary>
    public long WrittenRows => Interlocked.Read(ref _writtenRows);

    /// <summary>Number of batches currently in the queue (for backpressure).</summary>
    public int QueuedBatchCount => Volatile.Read(ref _queuedBatchCount);

    private long _materializedRows;
    private long _writtenRows;
    private int _queuedBatchCount;

    /// <summary>Called by producers when a batch is added to the queue.</summary>
    public void AddMaterialized(int count) => Interlocked.Add(ref _materializedRows, count);

    /// <summary>Called by the writer when a batch is written.</summary>
    public void AddWritten(int count) => Interlocked.Add(ref _writtenRows, count);

    /// <summary>Called when a batch is enqueued.</summary>
    public void IncrementQueuedBatches() => Interlocked.Increment(ref _queuedBatchCount);

    /// <summary>Called when a batch is dequeued by the writer.</summary>
    public void DecrementQueuedBatches() => Interlocked.Decrement(ref _queuedBatchCount);
}

/// <summary>
/// Summary counts and timings returned from the export pipeline for CLI reporting.
/// Row counts match <see cref="RawSnapshotData"/> list counts; timings are in milliseconds.
/// </summary>
public sealed class ExportCounts
{
    /// <summary>Number of native objects written.</summary>
    public int NativeObjects;

    /// <summary>Number of managed objects written.</summary>
    public int ManagedObjects;

    /// <summary>Number of connections written.</summary>
    public int Connections;

    /// <summary>Number of native roots written.</summary>
    public int NativeRoots;

    /// <summary>Number of memory regions written.</summary>
    public int MemoryRegions;

    /// <summary>Number of native allocations written.</summary>
    public int NativeAllocations;

    /// <summary>Number of OS system memory regions written.</summary>
    public int SystemMemoryRegions;

    /// <summary>Time spent materializing batches (ms).</summary>
    public long MaterializeMs;

    /// <summary>Time spent in the writer (ms).</summary>
    public long WriteMs;

    /// <summary>Total export time (ms); typically set by the CLI after the run.</summary>
    public long TotalMs;

    /// <summary>Backend total insert time (ms).</summary>
    public long BackendInsertMs;

    /// <summary>Backend commit time (ms).</summary>
    public long BackendCommitMs;

    /// <summary>Backend index build time (ms).</summary>
    public long BackendIndexBuildMs;

    /// <summary>Per-table insert times (ms).</summary>
    public long NativeObjectInsertMs;

    /// <summary>Per-table insert times (ms).</summary>
    public long ManagedObjectInsertMs;

    /// <summary>Per-table insert times (ms).</summary>
    public long ConnectionInsertMs;

    /// <summary>Per-table insert times (ms).</summary>
    public long NativeRootInsertMs;

    /// <summary>Per-table insert times (ms).</summary>
    public long MemoryRegionInsertMs;

    /// <summary>Per-table insert times (ms).</summary>
    public long NativeAllocationInsertMs;

    /// <summary>Per-table insert times (ms).</summary>
    public long SystemMemoryRegionInsertMs;
}

/// <summary>
/// Per-run statistics returned by <see cref="ExportDestination.IExportDestinationWriter.ConsumeAndWrite"/>:
/// row counts and timings for inserts, commit, and index build.
/// </summary>
public sealed class WriteStats
{
    /// <summary>Rows written per table.</summary>
    public long NativeObjectRows;

    /// <summary>Rows written per table.</summary>
    public long ManagedObjectRows;

    /// <summary>Rows written per table.</summary>
    public long ConnectionRows;

    /// <summary>Rows written per table.</summary>
    public long NativeRootRows;

    /// <summary>Rows written per table.</summary>
    public long MemoryRegionRows;

    /// <summary>Rows written per table.</summary>
    public long NativeAllocationRows;

    /// <summary>Rows written per table.</summary>
    public long SystemMemoryRegionRows;

    /// <summary>Insert time per table (ms).</summary>
    public long NativeObjectInsertMs;

    /// <summary>Insert time per table (ms).</summary>
    public long ManagedObjectInsertMs;

    /// <summary>Insert time per table (ms).</summary>
    public long ConnectionInsertMs;

    /// <summary>Insert time per table (ms).</summary>
    public long NativeRootInsertMs;

    /// <summary>Insert time per table (ms).</summary>
    public long MemoryRegionInsertMs;

    /// <summary>Insert time per table (ms).</summary>
    public long NativeAllocationInsertMs;

    /// <summary>Insert time per table (ms).</summary>
    public long SystemMemoryRegionInsertMs;

    /// <summary>Total time spent in inserts (ms).</summary>
    public long TotalInsertMs;

    /// <summary>Commit/sync time (ms).</summary>
    public long CommitMs;

    /// <summary>Index build time (ms).</summary>
    public long IndexBuildMs;
}

using System.Collections.Concurrent;

namespace MemorySnapshotDataTools.ExportDestination;

/// <summary>
/// Abstraction for writing snapshot data to a database. Implementations (e.g. DuckDB, SQLite) consume <see cref="WriteBatch"/> from a queue,
/// write to the given path, update <see cref="PipelineState"/>, and optionally support post-write validation.
/// </summary>
public interface IExportDestinationWriter
{
    /// <summary>Display name of the destination (e.g. "DuckDB", "SQLite") for progress and errors.</summary>
    string DestinationName { get; }

    /// <summary>
    /// Consumes batches from the queue until <see cref="BlockingCollection{T}.IsAddingCompleted"/> is true, writes all tables to the database,
    /// and returns per-table row counts and timings. Updates <paramref name="state"/> as batches are written.
    /// </summary>
    /// <param name="dbPath">Output database file path.</param>
    /// <param name="snapshotInfo">Metadata to write (e.g. to snapshot_info table).</param>
    /// <param name="queue">Bounded queue of write batches; adding is completed by the pipeline when producers finish.</param>
    /// <param name="state">Shared state to update (written rows, queued batch count).</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>Per-table row counts and insert/commit/index timings.</returns>
    WriteStats ConsumeAndWrite(
        string dbPath,
        SnapshotInfo snapshotInfo,
        BlockingCollection<WriteBatch> queue,
        PipelineState state,
        CancellationToken token);

    /// <summary>
    /// Writes the MemoryProfiler summary metrics into the <c>summary_metrics</c> table. Called after the
    /// main pipeline write (which creates the schema) and before validation.
    /// </summary>
    /// <param name="dbPath">Output database file path.</param>
    /// <param name="metrics">Summary metrics computed during extraction.</param>
    void WriteSummaryMetrics(string dbPath, SummaryMetrics metrics);

    /// <summary>
    /// Runs optional validation on the written database (e.g. row count checks, referential integrity) according to <paramref name="mode"/>.
    /// </summary>
    /// <param name="dbPath">Path to the database file.</param>
    /// <param name="rawData">Original snapshot data used for expected counts.</param>
    /// <param name="mode">Validation level (none, minimal, full).</param>
    void Validate(string dbPath, RawSnapshotData rawData, ValidationMode mode);
}

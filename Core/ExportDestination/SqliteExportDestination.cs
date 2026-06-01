using System.Collections.Concurrent;

namespace MemorySnapshotDataTools.ExportDestination;

/// <summary>
/// SQLite implementation of <see cref="IExportDestinationWriter"/>. Delegates to <see cref="SqliteWriter"/> for writing and validation.
/// Writes snapshot tables to a .db file with WAL mode and bulk inserts.
/// </summary>
internal sealed class SqliteExportDestination : IExportDestinationWriter
{
    /// <inheritdoc/>
    public string DestinationName => "sqlite";

    /// <inheritdoc/>
    public WriteStats ConsumeAndWrite(
        string dbPath,
        SnapshotInfo snapshotInfo,
        BlockingCollection<WriteBatch> queue,
        PipelineState state,
        CancellationToken token)
        => SqliteWriter.ConsumeAndWrite(dbPath, snapshotInfo, queue, state, token);

    /// <inheritdoc/>
    public void WriteSummaryMetrics(string dbPath, SummaryMetrics metrics)
        => SqliteWriter.WriteSummaryMetrics(dbPath, metrics);

    /// <inheritdoc/>
    public void Validate(string dbPath, RawSnapshotData rawData, ValidationMode mode)
        => SqliteWriter.Validate(dbPath, rawData, mode);
}

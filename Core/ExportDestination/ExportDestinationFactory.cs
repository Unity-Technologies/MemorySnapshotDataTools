namespace MemorySnapshotDataTools.ExportDestination;

/// <summary>
/// Factory for creating the appropriate <see cref="IExportDestinationWriter"/> based on <see cref="DestinationKind"/>.
/// </summary>
public static class ExportDestinationFactory
{
    /// <summary>Creates a writer for the specified database backend.</summary>
    /// <param name="kind">DuckDB or SQLite.</param>
    /// <returns>An implementation of <see cref="IExportDestinationWriter"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="kind"/> is not a known value.</exception>
    public static IExportDestinationWriter Create(DestinationKind kind) => kind switch
    {
        DestinationKind.DuckDb => new DuckDbExportDestination(),
        DestinationKind.Sqlite => new SqliteExportDestination(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

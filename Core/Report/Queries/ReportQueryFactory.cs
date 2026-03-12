namespace MemorySnapshotDataTools.Report.Queries;

/// <summary>
/// Creates an <see cref="IReportQueryBackend"/> based on the database file extension (.duckdb, .db, .sqlite, .sqlite3).
/// If extension is unknown, tries DuckDB first, then falls back to SQLite.
/// </summary>
internal static class ReportQueryFactory
{
    /// <summary>Opens the database at the given path and returns the appropriate query backend.</summary>
    /// <param name="dbPath">Path to the exported database file.</param>
    /// <returns>A backend connected to the database; caller must dispose.</returns>
    public static IReportQueryBackend Create(string dbPath)
    {
        var ext = Path.GetExtension(dbPath).ToLowerInvariant();
        return ext switch
        {
            ".duckdb" => new DuckDbReportQueries(dbPath),
            ".db" or ".sqlite" or ".sqlite3" => new SqliteReportQueries(dbPath),
            _ => TryOpenAsDuckDb(dbPath),
        };
    }

    private static IReportQueryBackend TryOpenAsDuckDb(string dbPath)
    {
        try
        {
            return new DuckDbReportQueries(dbPath);
        }
        catch
        {
            return new SqliteReportQueries(dbPath);
        }
    }
}

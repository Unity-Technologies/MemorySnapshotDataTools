using System.Data.Common;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;

namespace MemorySnapshotDataTools.ExportDestination;

/// <summary>
/// Schema status of an exported database relative to the current build (see <see cref="DatabaseSchemaInfo"/>).
/// </summary>
/// <param name="Major">Stored major version (0 = pre-versioning).</param>
/// <param name="Minor">Stored minor version.</param>
/// <param name="Action">Recommended action.</param>
/// <param name="SnapshotPath">Original <c>.snap</c> path from <c>snapshot_info</c>, when available.</param>
/// <param name="IsSqlite">True if the database is SQLite (affects the re-export command).</param>
/// <param name="DatabasePath">Path to the inspected database (used to build the re-export command).</param>
public readonly record struct SchemaStatus(
    int Major, int Minor, SchemaAction Action, string? SnapshotPath, bool IsSqlite, string DatabasePath)
{
    /// <summary>True when the source snapshot still exists on disk, so a re-export can be offered/run.</summary>
    public bool SnapshotExists => !string.IsNullOrEmpty(SnapshotPath) && File.Exists(SnapshotPath);

    /// <summary>The exact CLI <c>export</c> command to re-export this database, or null when the snapshot path is unknown.</summary>
    public string? ReExportCommand => string.IsNullOrEmpty(SnapshotPath)
        ? null
        : DatabaseSchemaInfo.BuildReExportCommand(SnapshotPath!, DatabasePath, IsSqlite);
}

/// <summary>
/// Path-based schema inspection and in-place upgrade for exported databases. Keeps all DB-connection
/// handling in Core so callers (e.g. the CLI) only deal with paths and the resulting <see cref="SchemaStatus"/>.
/// </summary>
public static class DatabaseMaintenance
{
    /// <summary>
    /// Opens the database read-only, reads its schema version and source snapshot path, and classifies it.
    /// Never throws for a readable file; an unreadable/locked file surfaces as the underlying exception.
    /// </summary>
    /// <param name="dbPath">Path to the exported database (.duckdb / .db).</param>
    /// <returns>The schema status.</returns>
    public static SchemaStatus Inspect(string dbPath)
    {
        var isSqlite = IsSqlitePath(dbPath);
        using var connection = OpenReadOnly(dbPath, isSqlite);
        connection.Open();
        var (major, minor) = DatabaseSchemaInfo.ReadVersion(connection);
        var snapshotPath = DatabaseSchemaInfo.ReadSnapshotPath(connection);
        return new SchemaStatus(major, minor, DatabaseSchemaInfo.Evaluate(major, minor), snapshotPath, isSqlite, dbPath);
    }

    /// <summary>
    /// Performs an in-place minor schema upgrade (re-applies views and indexes, bumps the minor version).
    /// Only valid for a database whose major version matches the current build; callers should check
    /// <see cref="SchemaStatus.Action"/> is <see cref="SchemaAction.UpgradeInPlace"/> first.
    /// </summary>
    /// <param name="dbPath">Path to the database to upgrade.</param>
    public static void UpgradeInPlace(string dbPath)
    {
        var kind = IsSqlitePath(dbPath) ? DestinationKind.Sqlite : DestinationKind.DuckDb;
        ExportDestinationFactory.Create(kind).UpgradeSchema(dbPath);
    }

    private static bool IsSqlitePath(string dbPath) =>
        Path.GetExtension(dbPath).ToLowerInvariant() is ".db" or ".sqlite" or ".sqlite3";

    private static DbConnection OpenReadOnly(string dbPath, bool isSqlite) => isSqlite
        ? new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly")
        : new DuckDBConnection($"Data Source={dbPath};ACCESS_MODE=READ_ONLY");
}

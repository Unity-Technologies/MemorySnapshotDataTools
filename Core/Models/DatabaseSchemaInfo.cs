using System.Data.Common;
using System.Reflection;

namespace MemorySnapshotDataTools;

/// <summary>
/// What a consumer should do about an exported database whose schema version differs from this build.
/// </summary>
public enum SchemaAction
{
    /// <summary>Schema matches the current major and minor version; nothing to do.</summary>
    None,

    /// <summary>
    /// Same major version, older minor: only views/indexes changed. The tool can upgrade the database
    /// in place (re-apply views and indexes) without re-parsing the snapshot.
    /// </summary>
    UpgradeInPlace,

    /// <summary>
    /// Older (or pre-versioning) major version: tables/columns changed. The database must be re-exported
    /// from the original <c>.snap</c>; it cannot be upgraded in place.
    /// </summary>
    ReExport,

    /// <summary>Database was written by a newer build of the tool than this one; upgrade the tool.</summary>
    ToolOutdated,
}

/// <summary>
/// Single source of truth for the exported-database schema version, stored in the <c>schema_meta</c>
/// table (<c>schema_version_major</c>, <c>schema_version_minor</c>) by both writers and checked by the CLI.
/// </summary>
/// <remarks>
/// <para>
/// The version has two parts:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Major</b> (<see cref="SchemaMajor"/>) — the table/column structure. Bump it for any change that
/// alters tables or columns (add/rename/remove a column or table, or change a column's meaning/units).
/// A database with a lower major <b>requires a re-export</b> from the original snapshot
/// (<see cref="SchemaAction.ReExport"/>); it cannot be upgraded in place.
/// </description></item>
/// <item><description>
/// <b>Minor</b> (<see cref="SchemaMinor"/>) — the set of derived objects (analysis <i>views</i> and
/// <i>indexes</i>) layered on top of the tables. Bump it when you add/change a view or index. A database
/// with the current major but a lower minor can be <b>upgraded in place</b>
/// (<see cref="SchemaAction.UpgradeInPlace"/>) by re-running the view/index DDL — no re-export needed.
/// </description></item>
/// </list>
/// <para>
/// Reset minor to 0 whenever you bump major. Mirror every change in <c>docs/database-schema.md</c>
/// (see the <c>memory-db-sql</c> Claude skill for the checklist).
/// </para>
/// </remarks>
public static class DatabaseSchemaInfo
{
    /// <summary>Current major schema version (table/column structure). A lower major requires re-export.</summary>
    public const int SchemaMajor = 1;

    /// <summary>Current minor schema version (views/indexes). A lower minor can be upgraded in place.</summary>
    public const int SchemaMinor = 2;

    /// <summary>Name used in advisories to refer to the CLI tool.</summary>
    public const string ToolName = "MemorySnapshotDataTools";

    /// <summary>Version of the MemorySnapshotDataTools build, recorded in <c>schema_meta.msdt_version</c>.</summary>
    public static string ToolVersion { get; } =
        typeof(DatabaseSchemaInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DatabaseSchemaInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Classifies a database's stored (major, minor) version against this build.</summary>
    /// <param name="major">Value from <c>schema_meta.schema_version_major</c>, or 0 if the table is absent.</param>
    /// <param name="minor">Value from <c>schema_meta.schema_version_minor</c>, or 0 if absent.</param>
    /// <returns>The recommended action.</returns>
    public static SchemaAction Evaluate(int major, int minor)
    {
        if (major > SchemaMajor || (major == SchemaMajor && minor > SchemaMinor))
            return SchemaAction.ToolOutdated;
        if (major < SchemaMajor)
            return SchemaAction.ReExport;       // includes major == 0 (pre-versioning databases)
        if (minor < SchemaMinor)
            return SchemaAction.UpgradeInPlace; // major == SchemaMajor, behind on views/indexes
        return SchemaAction.None;
    }

    /// <summary>
    /// Formats a stored (major, minor) version for display, appending a short advisory when it differs
    /// from this build. Used by the summary, report, and multi-report outputs.
    /// </summary>
    /// <param name="major">Stored major version (0 = pre-versioning).</param>
    /// <param name="minor">Stored minor version.</param>
    /// <returns>A display string such as <c>"1.1"</c> or <c>"1.0 (upgrade available → 1.1)"</c>.</returns>
    public static string DescribeVersion(int major, int minor)
    {
        if (major == 0)
            return "unversioned (re-export recommended)";

        var current = $"{SchemaMajor}.{SchemaMinor}";
        var stored = $"{major}.{minor}";
        return Evaluate(major, minor) switch
        {
            SchemaAction.None => stored,
            SchemaAction.UpgradeInPlace => $"{stored} (upgrade available → {current})",
            SchemaAction.ReExport => $"{stored} (re-export recommended → {current})",
            SchemaAction.ToolOutdated => $"{stored} (newer than tool {current})",
            _ => stored,
        };
    }

    /// <summary>
    /// Reads <c>schema_meta</c> from an open connection. Returns (0, 0) for pre-versioning databases that
    /// lack the table (or the major/minor columns), which <see cref="Evaluate"/> treats as
    /// <see cref="SchemaAction.ReExport"/>.
    /// </summary>
    /// <param name="connection">An open DuckDB or SQLite connection.</param>
    /// <returns>The stored (major, minor) version.</returns>
    public static (int Major, int Minor) ReadVersion(DbConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT schema_version_major, schema_version_minor FROM schema_meta LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var major = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                var minor = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                return (major, minor);
            }
        }
        catch (DbException)
        {
            // Pre-versioning database: no schema_meta table or no major/minor columns.
        }

        return (0, 0);
    }

    /// <summary>Reads <c>snapshot_info.snapshot_path</c> (the original <c>.snap</c> path), or null when unavailable.</summary>
    /// <param name="connection">An open DuckDB or SQLite connection.</param>
    /// <returns>The source snapshot path, or null.</returns>
    public static string? ReadSnapshotPath(DbConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT snapshot_path FROM snapshot_info LIMIT 1";
            return cmd.ExecuteScalar() as string;
        }
        catch (DbException)
        {
            return null;
        }
    }

    /// <summary>Builds the exact CLI <c>export</c> command a user should run to re-export a database.</summary>
    /// <param name="snapshotPath">Path to the source <c>.snap</c> (from <c>snapshot_info.snapshot_path</c>).</param>
    /// <param name="databasePath">Destination database path to overwrite.</param>
    /// <param name="sqlite">True if the destination is SQLite (adds <c>--destination sqlite</c>).</param>
    /// <returns>A copy-pasteable command string.</returns>
    public static string BuildReExportCommand(string snapshotPath, string databasePath, bool sqlite)
    {
        var dest = sqlite ? " --destination sqlite" : string.Empty;
        return $"{ToolName} export \"{snapshotPath}\" \"{databasePath}\"{dest}";
    }
}

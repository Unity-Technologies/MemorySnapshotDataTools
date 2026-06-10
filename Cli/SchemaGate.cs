using MemorySnapshotDataTools.Export;
using MemorySnapshotDataTools.ExportDestination;

namespace MemorySnapshotDataTools.Cli;

/// <summary>
/// Checks an exported database's schema version before a read command (report/summary/validate) and,
/// when it is behind the current build, informs the user and — interactively — offers to upgrade it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Minor behind</b> (new views/indexes only): offers an in-place upgrade
/// (<see cref="DatabaseMaintenance.UpgradeInPlace"/>), which is safe and fast.</description></item>
/// <item><description><b>Major behind / pre-versioning</b> (table structure changed): a re-export from the
/// original <c>.snap</c> is required. If the snapshot still exists at the recorded path, offers to run it;
/// otherwise prints the exact <c>export</c> command.</description></item>
/// </list>
/// Non-interactive sessions (stdin redirected) never auto-modify the database: the gate prints the
/// advisory and the command to run, then proceeds with the existing database.
/// </remarks>
internal static class SchemaGate
{
    private const string DatabaseExtensions = ".duckdb,.db,.sqlite,.sqlite3";

    /// <summary>Runs the schema check for a database path. Always proceeds (returns nothing); advisory only.</summary>
    public static void Check(string dbPath)
    {
        if (!LooksLikeDatabase(dbPath) || !File.Exists(dbPath))
            return;

        SchemaStatus status;
        try
        {
            status = DatabaseMaintenance.Inspect(dbPath);
        }
        catch
        {
            // Never block the requested command because the version probe failed (e.g. locked file).
            return;
        }

        var current = $"v{DatabaseSchemaInfo.SchemaMajor}.{DatabaseSchemaInfo.SchemaMinor}";
        var found = $"v{status.Major}.{status.Minor}";

        switch (status.Action)
        {
            case SchemaAction.None:
                break;

            case SchemaAction.ToolOutdated:
                Console.Error.WriteLine(
                    $"Note: database schema {found} is newer than this build ({current}). " +
                    $"Update {DatabaseSchemaInfo.ToolName} for full support.");
                break;

            case SchemaAction.UpgradeInPlace:
                HandleUpgradeInPlace(status, current, found);
                break;

            case SchemaAction.ReExport:
                HandleReExport(status, current, found);
                break;
        }
    }

    private static void HandleUpgradeInPlace(SchemaStatus status, string current, string found)
    {
        Console.Error.WriteLine(
            $"Database schema {found} is behind {current} — newer analysis views/indexes are available.");

        if (Confirm("Upgrade this database in place now?", defaultYes: true))
        {
            DatabaseMaintenance.UpgradeInPlace(status.DatabasePath);
            Console.Error.WriteLine($"Upgraded database schema to {current}.");
            foreach (var change in DatabaseSchemaInfo.ChangesSince(status.Major, status.Minor))
                Console.Error.WriteLine($"  • {change}");
        }
        else
        {
            Console.Error.WriteLine($"  To upgrade later: {DatabaseSchemaInfo.ToolName} upgrade \"{status.DatabasePath}\"");
        }
    }

    private static void HandleReExport(SchemaStatus status, string current, string found)
    {
        var versionDesc = status.Major == 0 ? "a pre-versioning schema" : $"schema {found}";
        Console.Error.WriteLine(
            $"Database has {versionDesc}; the current major version is v{DatabaseSchemaInfo.SchemaMajor}. " +
            "Its table structure is outdated and it must be re-exported from the original snapshot.");

        if (status.SnapshotExists)
        {
            if (Confirm($"Re-export now from {status.SnapshotPath}?", defaultYes: false))
            {
                var code = ExportRunner.Run(
                    status.SnapshotPath!,
                    status.DatabasePath,
                    new ExportRunOptions(),
                    status.IsSqlite ? DestinationKind.Sqlite : DestinationKind.DuckDb,
                    new ConsoleProgress(verbose: true),
                    CancellationToken.None);
                Console.Error.WriteLine(code == 0
                    ? "Re-export complete."
                    : "Re-export failed; continuing with the existing database.");
            }
            else
            {
                Console.Error.WriteLine($"  To re-export later: {status.ReExportCommand}");
            }
        }
        else
        {
            var where = string.IsNullOrEmpty(status.SnapshotPath) ? string.Empty : $" at {status.SnapshotPath}";
            var cmd = status.ReExportCommand
                      ?? $"{DatabaseSchemaInfo.ToolName} export <snapshot.snap> \"{status.DatabasePath}\"";
            Console.Error.WriteLine($"  Original snapshot not found{where}. Re-export with: {cmd}");
        }
    }

    /// <summary>Prompts y/n on an interactive terminal; in non-interactive sessions returns false (advisory only).</summary>
    private static bool Confirm(string prompt, bool defaultYes)
    {
        if (Console.IsInputRedirected)
            return false;

        Console.Error.Write($"{prompt} [{(defaultYes ? "Y/n" : "y/N")}] ");
        var line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
            return defaultYes;
        return line.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDatabase(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return DatabaseExtensions.Split(',').Contains(ext);
    }
}

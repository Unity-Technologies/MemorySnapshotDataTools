using System.Data.Common;
using System.Diagnostics;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using MemorySnapshotDataTools.Parser;
using MemorySnapshotDataTools.Validation;

namespace MemorySnapshotDataTools.Report;

/// <summary>Where a <see cref="SummaryReport"/> was produced from.</summary>
public enum SummarySource
{
    /// <summary>Decoded directly from a <c>.snap</c> file.</summary>
    Snapshot,

    /// <summary>Read from an exported <c>.duckdb</c> or <c>.db</c> database.</summary>
    Database,
}

/// <summary>Options for the <c>summary</c> command.</summary>
public sealed class SummaryRunOptions
{
    /// <summary>Path to a <c>.snap</c> snapshot or an exported <c>.duckdb</c>/<c>.db</c> database.</summary>
    public string InputPath { get; set; } = string.Empty;

    /// <summary>Print progress while decoding a snapshot.</summary>
    public bool Verbose { get; set; }
}

/// <summary>
/// A high-level memory-usage summary for one snapshot: capture metadata plus the MemoryProfiler
/// "Summary" page metrics. Populated either by decoding a <c>.snap</c> file or by reading the
/// <c>summary_metrics</c>/<c>snapshot_info</c> tables of an exported database.
/// </summary>
public sealed class SummaryReport
{
    /// <summary>Path the summary was produced from.</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>Whether the source was a snapshot or a database.</summary>
    public SummarySource Source { get; init; }

    /// <summary>Capture metadata (product, platform, Unity version, session).</summary>
    public SnapshotInfo Info { get; init; } = new();

    /// <summary>Allocated Memory Distribution + Managed Heap Utilization breakdowns and totals.</summary>
    public SummaryMetrics Metrics { get; init; } = new();

    /// <summary>
    /// Unity object types ranked by total native allocated bytes (Memory Profiler "Unity Objects" view,
    /// grouped by type), sorted descending. Empty when no native objects are present.
    /// </summary>
    public IReadOnlyList<UnityObjectCategory> UnityObjectCategories { get; init; } = [];

    /// <summary>False when a database lacked a <c>summary_metrics</c> table (export with the current tool).</summary>
    public bool SummaryAvailable { get; init; } = true;

    /// <summary>
    /// Schema version display (e.g. "1.1", or with a re-export/upgrade advisory when behind). For a
    /// snapshot source there is no exported database yet, so this notes the version a fresh export would write.
    /// </summary>
    public string SchemaVersion { get; init; } = string.Empty;
}

/// <summary>
/// Entry point for the <c>summary</c> command. Reports a high-level memory summary to the console
/// without generating a database. Reuses <see cref="SnapshotBridge.ExtractRawData"/> for snapshots and
/// the golden-validation summary queries for already-exported databases.
/// </summary>
public static class SummaryReportRunner
{
    /// <summary>
    /// Builds and prints a <see cref="SummaryReport"/> for <see cref="SummaryRunOptions.InputPath"/>.
    /// </summary>
    /// <returns>0 on success, 1 when the input is missing or unsupported.</returns>
    public static int Run(SummaryRunOptions options, IProgressReporter progress, CancellationToken token = default)
    {
        var path = options.InputPath;
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Input file not found: {path}");
            return 1;
        }

        var extension = Path.GetExtension(path);
        var stopwatch = Stopwatch.StartNew();
        SummaryReport report;
        try
        {
            if (extension.Equals(".snap", StringComparison.OrdinalIgnoreCase))
            {
                progress.Report($"Summarizing snapshot {Path.GetFileName(path)} (full decode + managed crawl)...", force: true);
                report = FromSnapshot(path, progress, token);
            }
            else if (extension.Equals(".duckdb", StringComparison.OrdinalIgnoreCase)
                     || extension.Equals(".db", StringComparison.OrdinalIgnoreCase))
            {
                progress.Report($"Reading summary from database {Path.GetFileName(path)}...", force: true);
                report = FromDatabase(path, extension);
            }
            else
            {
                Console.Error.WriteLine(
                    $"Unsupported input '{extension}'. Provide a .snap snapshot or a .duckdb/.db database.");
                return 1;
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Summary cancelled.");
            return 2;
        }

        stopwatch.Stop();
        Console.Write(SummaryReportFormatter.Format(report));
        progress.Report($"Summary completed in {stopwatch.Elapsed.TotalSeconds:F1}s.", force: true);
        return 0;
    }

    /// <summary>
    /// Decodes a <c>.snap</c> file through the same extraction pipeline used by export and validation,
    /// then reads the computed <see cref="SummaryMetrics"/> off the result.
    /// </summary>
    private static SummaryReport FromSnapshot(string snapshotPath, IProgressReporter progress, CancellationToken token)
    {
        // Surface decode progress even without --verbose so a long-running summary isn't silent.
        var data = SnapshotBridge.ExtractRawData(snapshotPath, new ForcingProgressReporter(progress), token);
        return new SummaryReport
        {
            SourcePath = snapshotPath,
            Source = SummarySource.Snapshot,
            Info = data.SnapshotInfo,
            Metrics = data.SummaryMetrics,
            UnityObjectCategories = Report.UnityObjectCategories.FromNativeObjects(data.NativeObjects),
            SummaryAvailable = true,
            SchemaVersion = $"{DatabaseSchemaInfo.SchemaMajor}.{DatabaseSchemaInfo.SchemaMinor} (on export)",
        };
    }

    /// <summary>
    /// Reads <c>snapshot_info</c> and <c>summary_metrics</c> from an exported database (no decode).
    /// </summary>
    private static SummaryReport FromDatabase(string databasePath, string extension)
    {
        // Read-only: the summary path only reads from the database. See docs/sql-safety.md.
        using DbConnection connection = extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
            ? new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly")
            : new DuckDBConnection($"Data Source={databasePath};ACCESS_MODE=READ_ONLY");
        connection.Open();

        var info = ReadSnapshotInfo(connection);
        var (metrics, available) = ReadSummaryMetrics(connection);
        var categories = ReadUnityObjectCategories(connection);
        var (major, minor) = DatabaseSchemaInfo.ReadVersion(connection);

        return new SummaryReport
        {
            SourcePath = databasePath,
            Source = SummarySource.Database,
            Info = info,
            Metrics = metrics,
            UnityObjectCategories = categories,
            SummaryAvailable = available,
            SchemaVersion = DatabaseSchemaInfo.DescribeVersion(major, minor),
        };
    }

    private static SnapshotInfo ReadSnapshotInfo(DbConnection connection)
    {
        var info = new SnapshotInfo();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM snapshot_info LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return info;

            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                columns[reader.GetName(i)] = i;

            string Str(string name) =>
                columns.TryGetValue(name, out var o) && !reader.IsDBNull(o)
                    ? reader.GetValue(o)?.ToString() ?? string.Empty
                    : string.Empty;
            uint Num(string name) =>
                columns.TryGetValue(name, out var o) ? (uint)DbScalarReader.GetInt64(reader, o) : 0u;

            info.SnapshotPath = Str("snapshot_path");
            info.ExportedAtUtc = Str("exported_at_utc");
            info.UnityVersion = Str("unity_version");
            info.SnapFormatVersion = Num("snap_format_version");
            info.SessionGuid = Num("session_guid");
            info.ProductName = Str("product_name");
            info.Platform = Str("platform");
            info.RecordDateUtc = Str("record_date_utc");
        }
        catch (DbException)
        {
            // Older databases may lack snapshot_info; leave metadata blank.
        }

        return info;
    }

    private static (SummaryMetrics Metrics, bool Available) ReadSummaryMetrics(DbConnection connection)
    {
        var metrics = new SummaryMetrics();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = GoldenValidationQueries.SummaryMetricsSql;
            using var reader = cmd.ExecuteReader();

            var any = false;
            while (reader.Read())
            {
                any = true;
                var group = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var category = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var committed = ToULong(DbScalarReader.GetInt64(reader, 2));
                var resident = ToULong(DbScalarReader.GetInt64(reader, 3));
                var residentAvailable = DbScalarReader.GetInt64(reader, 4) != 0;

                if (group == SummaryMetricsTable.GroupTotals && category == SummaryMetricsTable.CategoryTotal)
                {
                    metrics.TotalAllocatedBytes = committed;
                    metrics.TotalResidentBytes = resident;
                }
                else if (group == SummaryMetricsTable.GroupAllocatedMemoryDistribution)
                {
                    metrics.AllocatedMemoryDistribution.Add(MakeCategory(category, committed, resident, residentAvailable));
                }
                else if (group == SummaryMetricsTable.GroupManagedHeapUtilization)
                {
                    metrics.ManagedHeapUtilization.Add(MakeCategory(category, committed, resident, residentAvailable));
                }
            }

            return (metrics, any);
        }
        catch (DbException)
        {
            // summary_metrics table absent (database exported by an older tool version).
            return (metrics, false);
        }
    }

    private const string UnityObjectCategoriesSql = """
        SELECT COALESCE(native_type_name, '(unknown)') AS type_name,
               COUNT(*) AS obj_count,
               COALESCE(SUM(size_bytes), 0) AS allocated_bytes
        FROM native_objects
        WHERE is_destroyed = false
        GROUP BY native_type_name
        ORDER BY allocated_bytes DESC, type_name
        """;

    private static List<UnityObjectCategory> ReadUnityObjectCategories(DbConnection connection)
    {
        var categories = new List<UnityObjectCategory>();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = UnityObjectCategoriesSql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                categories.Add(new UnityObjectCategory
                {
                    TypeName = reader.IsDBNull(0) ? "(unknown)" : reader.GetString(0),
                    Count = DbScalarReader.GetInt64(reader, 1),
                    AllocatedBytes = ToULong(DbScalarReader.GetInt64(reader, 2)),
                });
            }
        }
        catch (DbException)
        {
            // native_objects absent or unreadable; omit the section.
        }

        return categories;
    }

    private static SummaryCategory MakeCategory(string name, ulong committed, ulong resident, bool residentAvailable) =>
        new()
        {
            Name = name,
            CommittedBytes = committed,
            ResidentBytes = resident,
            ResidentAvailable = residentAvailable,
        };

    private static ulong ToULong(long value) => value < 0 ? 0UL : (ulong)value;

    /// <summary>Forwards every message as forced, so summary always shows decode progress.</summary>
    private sealed class ForcingProgressReporter(IProgressReporter inner) : IProgressReporter
    {
        public void Report(string message, bool force = false) => inner.Report(message, force: true);
    }
}

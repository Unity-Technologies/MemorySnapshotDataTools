using System.Globalization;
using MemorySnapshotDataTools.Report.Queries;

namespace MemorySnapshotDataTools.Gephi;

/// <summary>
/// Exports Gephi-compatible edge and node CSV from an exported snapshot database.
/// Supports native-object and managed-object edge lists; mixed mode is not yet implemented.
/// </summary>
public static class GephiExport
{
    private const string NativeEdgesSql =
        "SELECT from_index, to_index, connection_type FROM connections WHERE from_kind = 'native_object' AND to_kind = 'native_object'";

    private const string NativeNodesSql =
        "SELECT native_object_index, instance_id, name, native_type_name, size_bytes FROM native_objects";

    private const string ManagedEdgesSql =
        "SELECT from_index, to_index, connection_type FROM connections WHERE from_kind = 'managed_object' AND to_kind = 'managed_object'";

    private const string ManagedNodesSql =
        "SELECT managed_object_index, address, managed_type_name, size_bytes FROM managed_objects";

    /// <summary>
    /// Exports edge list (and optional nodes) from an exported snapshot database to Gephi CSV files.
    /// Mode "native" exports native-native connections and native_objects; mode "managed" exports managed-managed connections and managed_objects.
    /// </summary>
    /// <param name="dbPath">Path to the exported database (.duckdb or .db).</param>
    /// <param name="edgesPath">Path for the output edges CSV.</param>
    /// <param name="nodesPath">Optional path for the output nodes CSV; if null, only edges are written.</param>
    /// <param name="mode">Export mode: "native" or "managed"; "mixed" throws.</param>
    /// <param name="progress">Progress reporter for status messages.</param>
    /// <exception cref="NotSupportedException">When <paramref name="mode"/> is "mixed" or unknown.</exception>
    public static void RunFromDatabase(
        string dbPath,
        string edgesPath,
        string? nodesPath,
        string mode,
        IProgressReporter progress)
    {
        if (string.Equals(mode, "mixed", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Gephi export mode '{mode}' is not supported. Use 'native' or 'managed'.");

        progress.Report($"Opening database: {dbPath}", force: true);

        using var backend = ReportQueryFactory.Create(dbPath);
        progress.Report($"Backend: {backend.Dialect}", force: true);

        if (string.Equals(mode, "managed", StringComparison.OrdinalIgnoreCase))
        {
            RunManaged(backend, edgesPath, nodesPath, progress);
            return;
        }

        RunNative(backend, edgesPath, nodesPath, progress);
    }

    private static void RunNative(IReportQueryBackend backend, string edgesPath, string? nodesPath, IProgressReporter progress)
    {
        progress.Report("Querying native-native connections...", force: true);
        var (_, edgeRows) = backend.ExecuteQuery(NativeEdgesSql);
        var edges = new List<(long FromIndex, long ToIndex, string Label)>(edgeRows.Count);
        foreach (var row in edgeRows)
        {
            var from = row.Length > 0 ? ToLong(row[0]) : 0L;
            var to = row.Length > 1 ? ToLong(row[1]) : 0L;
            var label = row.Length > 2 ? ToString(row[2]) : "native_connection";
            edges.Add((from, to, label));
        }

        List<(long Id, string Label, string Type, ulong Size)>? nodes = null;
        if (!string.IsNullOrEmpty(nodesPath))
        {
            progress.Report("Querying native objects for node list...", force: true);
            var (_, nodeRows) = backend.ExecuteQuery(NativeNodesSql);
            nodes = new List<(long Id, string Label, string Type, ulong Size)>(nodeRows.Count);
            foreach (var row in nodeRows)
            {
                var id = row.Length > 0 ? ToLong(row[0]) : 0L;
                var instanceId = row.Length > 1 ? ToString(row[1]) : "";
                var name = row.Length > 2 ? ToString(row[2]) : "";
                var typeName = row.Length > 3 ? ToString(row[3]) : "";
                var size = row.Length > 4 ? ToUInt64(row[4]) : 0UL;
                var label = !string.IsNullOrEmpty(name) ? name : instanceId;
                if (string.IsNullOrEmpty(label))
                    label = id.ToString(CultureInfo.InvariantCulture);
                nodes.Add((id, label, typeName, size));
            }
        }

        GephiEdgeListWriter.WriteNativeEdgeList(edges, edgesPath, nodes, nodesPath, progress);
    }

    private static void RunManaged(IReportQueryBackend backend, string edgesPath, string? nodesPath, IProgressReporter progress)
    {
        progress.Report("Querying managed-managed connections...", force: true);
        var (_, edgeRows) = backend.ExecuteQuery(ManagedEdgesSql);
        var edges = new List<(long FromIndex, long ToIndex, string Label)>(edgeRows.Count);
        foreach (var row in edgeRows)
        {
            var from = row.Length > 0 ? ToLong(row[0]) : 0L;
            var to = row.Length > 1 ? ToLong(row[1]) : 0L;
            var label = row.Length > 2 ? ToString(row[2]) : "managed_reference";
            edges.Add((from, to, label));
        }

        List<(long Id, string Label, string Type, ulong Size)>? nodes = null;
        if (!string.IsNullOrEmpty(nodesPath))
        {
            progress.Report("Querying managed objects for node list...", force: true);
            var (_, nodeRows) = backend.ExecuteQuery(ManagedNodesSql);
            nodes = new List<(long Id, string Label, string Type, ulong Size)>(nodeRows.Count);
            foreach (var row in nodeRows)
            {
                var id = row.Length > 0 ? ToLong(row[0]) : 0L;
                var address = row.Length > 1 ? ToUInt64(row[1]) : 0UL;
                var typeName = row.Length > 2 ? ToString(row[2]) : "";
                var sizeBytes = row.Length > 3 ? ToLong(row[3]) : 0L;
                var size = (ulong)Math.Max(0, sizeBytes);
                var label = !string.IsNullOrEmpty(typeName)
                    ? $"{typeName} (0x{address:X})"
                    : id.ToString(CultureInfo.InvariantCulture);
                nodes.Add((id, label, typeName, size));
            }
        }

        GephiEdgeListWriter.WriteNativeEdgeList(edges, edgesPath, nodes, nodesPath, progress);
    }

    private static long ToLong(object? value)
    {
        if (value == null) return 0;
        if (value is long l) return l;
        if (value is int i) return i;
        if (value is decimal d) return (long)d;
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static ulong ToUInt64(object? value)
    {
        if (value == null) return 0;
        if (value is ulong u) return u;
        if (value is long l) return (ulong)l;
        if (value is int i) return (ulong)i;
        if (value is decimal d) return (ulong)d;
        return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
    }

    private static string ToString(object? value) => value?.ToString() ?? "";
}

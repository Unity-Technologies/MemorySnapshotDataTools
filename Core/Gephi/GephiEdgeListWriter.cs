namespace MemorySnapshotDataTools.Gephi;

/// <summary>
/// Writes Gephi-compatible edge and node CSV files from in-memory data.
/// Source-agnostic so it can be used with database or snapshot input.
/// </summary>
public static class GephiEdgeListWriter
{
    /// <summary>
    /// Writes a native object edge list and optional node list to CSV files for Gephi import.
    /// </summary>
    /// <param name="edges">Edges as (Source index, Target index, edge label).</param>
    /// <param name="edgesPath">Path for the edges CSV (Source, Target, Label).</param>
    /// <param name="nodes">Optional node list (Id, Label, Type, Size); if null, no nodes file is written.</param>
    /// <param name="nodesPath">Path for the nodes CSV when <paramref name="nodes"/> is non-null; ignored otherwise.</param>
    /// <param name="progress">Optional progress reporter.</param>
    public static void WriteNativeEdgeList(
        IEnumerable<(long FromIndex, long ToIndex, string Label)> edges,
        string edgesPath,
        IEnumerable<(long Id, string Label, string Type, ulong Size)>? nodes,
        string? nodesPath,
        IProgressReporter? progress = null)
    {
        progress?.Report($"Writing edges to {edgesPath}", force: true);

        var dir = Path.GetDirectoryName(edgesPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using (var writer = new StreamWriter(edgesPath, false, System.Text.Encoding.UTF8))
        {
            writer.WriteLine("Source,Target,Label");
            foreach (var (from, to, label) in edges)
                writer.WriteLine($"{from},{to},{CsvEscape(label)}");
        }

        if (nodes != null && !string.IsNullOrEmpty(nodesPath))
        {
            progress?.Report($"Writing nodes to {nodesPath}", force: true);
            dir = Path.GetDirectoryName(nodesPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using (var writer = new StreamWriter(nodesPath, false, System.Text.Encoding.UTF8))
            {
                writer.WriteLine("Id,Label,Type,Size");
                foreach (var (id, label, type, size) in nodes)
                    writer.WriteLine($"{id},{CsvEscape(label)},{CsvEscape(type)},{size}");
            }
        }
    }

    /// <summary>
    /// Escapes a field for CSV: wraps in double quotes if the value contains comma, double quote, or newline; doubles internal quotes.
    /// </summary>
    internal static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

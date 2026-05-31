using System.Globalization;

namespace MemorySnapshotDataTools.Report.MultiSnapshotReport;

/// <summary>
/// Groups multi-snapshot report rows by Unity Memory Profiler session GUID, with filename fallback.
/// </summary>
public static class MultiSnapshotSessionGrouper
{
    /// <summary>
    /// Builds session groups ordered by earliest capture time in each session.
    /// </summary>
    public static IReadOnlyList<MultiSnapshotSessionGroup> BuildGroups(IReadOnlyList<SnapshotMetricsRow> rows)
    {
        if (rows.Count == 0)
            return [];

        var clusters = rows
            .GroupBy(BuildClusterKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SessionCluster(g.Key, g.ToList()))
            .OrderBy(c => c.SortKey)
            .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sessionNumber = 1;
        var groups = new List<MultiSnapshotSessionGroup>(clusters.Count);
        foreach (var cluster in clusters)
        {
            var first = cluster.Snapshots.OrderBy(s => s.SortTimestamp).First();
            groups.Add(new MultiSnapshotSessionGroup
            {
                SessionKey = cluster.Key,
                DisplayTitle = BuildDisplayTitle(sessionNumber, first, cluster.Snapshots.Count),
                SessionNumber = sessionNumber,
                PlatformKind = first.PlatformKind,
                Snapshots = cluster.Snapshots.OrderBy(s => s.SnapshotName, StringComparer.OrdinalIgnoreCase).ToList(),
            });
            sessionNumber++;
        }

        return groups;
    }

    /// <summary>
    /// Stable cluster key: profiler session GUID when present, otherwise filename-derived key.
    /// </summary>
    public static string BuildClusterKey(SnapshotMetricsRow row)
    {
        if (row.SessionGuid != CaptureMetadata.InvalidSessionGuid)
            return $"guid:{row.SessionGuid.ToString(CultureInfo.InvariantCulture)}";

        return MultiSnapshotSessionKey.FromFileName(row.SnapshotName, FormatUnityKey(row)).SessionKey;
    }

    /// <summary>
    /// Human-readable session header aligned with Memory Profiler (Session N · Product · platform · Unity).
    /// </summary>
    public static string BuildDisplayTitle(int sessionNumber, SnapshotMetricsRow representative, int snapshotCount)
    {
        var parts = new List<string> { $"Session {sessionNumber.ToString(CultureInfo.InvariantCulture)}" };

        var product = representative.ProductName;
        if (string.IsNullOrWhiteSpace(product))
            product = ExtractProjectPrefix(representative.SnapshotName);

        if (!string.IsNullOrWhiteSpace(product))
            parts.Add(product.Trim());

        var platformLabel = representative.PlatformKind.ToDisplayLabel();
        if (!string.IsNullOrWhiteSpace(platformLabel))
            parts.Add(platformLabel);
        else if (!string.IsNullOrWhiteSpace(representative.Platform))
            parts.Add(representative.Platform);

        var unityLabel = FormatUnityLabel(representative);
        if (!string.IsNullOrWhiteSpace(unityLabel))
            parts.Add(unityLabel);

        return string.Join(" · ", parts);
    }

    private static string FormatUnityKey(SnapshotMetricsRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.UnityVersion) && !row.UnityVersion.StartsWith("format:", StringComparison.OrdinalIgnoreCase))
            return row.UnityVersion;

        return row.SnapFormatVersion > 0
            ? $"format:{row.SnapFormatVersion.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
    }

    private static string FormatUnityLabel(SnapshotMetricsRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.UnityVersion) && !row.UnityVersion.StartsWith("format:", StringComparison.OrdinalIgnoreCase))
            return row.UnityVersion.Trim();

        return row.SnapFormatVersion > 0
            ? $"Snap format {row.SnapFormatVersion.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
    }

    private static string ExtractProjectPrefix(string snapshotName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            snapshotName,
            @"^(?<prefix>.+?)_\d{4}-\d{2}-\d{2}_",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["prefix"].Value : snapshotName;
    }

    private sealed class SessionCluster
    {
        public SessionCluster(string key, List<SnapshotMetricsRow> snapshots)
        {
            Key = key;
            Snapshots = snapshots;
            SortKey = snapshots.Min(s => s.SortTimestamp);
        }

        public string Key { get; }
        public List<SnapshotMetricsRow> Snapshots { get; }
        public DateTime SortKey { get; }
    }
}

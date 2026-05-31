using System;
using System.Linq;

/// <summary>
/// Shared helpers for golden extraction and tool validation aligned with Memory Profiler UI grouping.
/// </summary>
internal static class MemorySnapshotValidationHelpers
{
    /// <summary>Native object type tracked under Native → Unity Objects.</summary>
    public const string AssetBundleNativeTypeName = "AssetBundle";

    /// <summary>Metric label for SerializedFile rows grouped under Native → Unity Subsystems.</summary>
    public const string SerializedFileMetricName = "SerializedFile";

    /// <summary>
    /// Returns true when <paramref name="areaName"/> is the Unity Subsystems area used for SerializedFile roots
    /// (same grouping as Memory Profiler All Tracked Memory → Native → Unity Subsystems).
    /// </summary>
    public static bool IsSerializedFileSubsystemArea(string areaName)
    {
        if (string.IsNullOrWhiteSpace(areaName))
            return false;

        var normalized = new string(areaName
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToLowerInvariant();

        return normalized is "serializedfile" or "serializedfiles"
            || normalized.Contains("serializedfile", StringComparison.Ordinal);
    }

    /// <summary>
    /// SQL predicate (SQLite/DuckDB) for SerializedFile subsystem rows in <c>native_roots</c>.
    /// </summary>
    public const string SerializedFileNativeRootsWhereClause =
        "LOWER(COALESCE(area_name, '')) LIKE '%serializedfile%'";
}

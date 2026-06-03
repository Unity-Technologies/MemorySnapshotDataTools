namespace MemorySnapshotDataTools.Report.MultiSnapshotReport;

/// <summary>
/// Metrics for a single native type within one snapshot database.
/// </summary>
public sealed class NativeTypeSnapshotMetrics
{
    /// <summary>Native type name (e.g. AssetBundle).</summary>
    public string NativeTypeName { get; init; } = string.Empty;

    /// <summary>Number of native objects of this type.</summary>
    public int Count { get; init; }

    /// <summary>Sum of allocated (size_bytes) in bytes.</summary>
    public long AllocatedBytes { get; init; }

    /// <summary>Sum of resident bytes, or null when not available for this capture.</summary>
    public long? ResidentBytes { get; init; }
}

/// <summary>
/// Metrics for a native root row (e.g. PersistentManager.Remapper) within one snapshot.
/// </summary>
public sealed class NativeRootSnapshotMetrics
{
    /// <summary>Root area name.</summary>
    public string AreaName { get; init; } = string.Empty;

    /// <summary>Root object name.</summary>
    public string ObjectName { get; init; } = string.Empty;

    /// <summary>Accumulated allocated bytes.</summary>
    public long AllocatedBytes { get; init; }

    /// <summary>Resident bytes, or null when not computable.</summary>
    public long? ResidentBytes { get; init; }
}

/// <summary>
/// Per-snapshot metrics loaded from one DuckDB or SQLite database file.
/// </summary>
public sealed record SnapshotMetricsRow
{
    /// <summary>Display name derived from the database filename.</summary>
    public string SnapshotName { get; init; } = string.Empty;

    /// <summary>Full path to the database file.</summary>
    public string DatabasePath { get; init; } = string.Empty;

    /// <summary>Session key for grouping (project, date, capture context, Unity/snap format).</summary>
    public string SessionKey { get; init; } = string.Empty;

    /// <summary>Capture date (YYYY-MM-DD) parsed from the filename.</summary>
    public string CaptureDate { get; init; } = string.Empty;

    /// <summary>Unity or snap format version from <c>snapshot_info</c> or snap metadata.</summary>
    public string UnityVersion { get; init; } = string.Empty;

    /// <summary>Snap file format version when known.</summary>
    public uint SnapFormatVersion { get; init; }

    /// <summary>Database schema version display (e.g. "1.1", or with an advisory when behind the current build).</summary>
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>True when the database schema matches the current build (no upgrade/re-export needed).</summary>
    public bool SchemaUpToDate { get; init; } = true;

    /// <summary>Profiler session GUID from snapshot metadata.</summary>
    public uint SessionGuid { get; init; }

    /// <summary>Product or project name from snapshot metadata.</summary>
    public string ProductName { get; init; } = string.Empty;

    /// <summary>Runtime platform name (e.g. IPhonePlayer).</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>Coarse platform for icons.</summary>
    public CapturePlatformKind PlatformKind { get; init; }

    /// <summary>Sortable capture timestamp (metadata or filename date).</summary>
    public DateTime SortTimestamp { get; init; }

    /// <summary>Per-type metrics keyed by type name.</summary>
    public IReadOnlyDictionary<string, NativeTypeSnapshotMetrics> NativeTypes { get; init; } =
        new Dictionary<string, NativeTypeSnapshotMetrics>();

    /// <summary>Remapper / PMR root metrics (may be empty).</summary>
    public IReadOnlyList<NativeRootSnapshotMetrics> RemapperRoots { get; init; } = [];
}

/// <summary>
/// Full model for a multi-snapshot HTML report.
/// </summary>
public sealed class MultiSnapshotReportModel
{
    /// <summary>Report title shown in the HTML header.</summary>
    public string Title { get; init; } = "Multi-Snapshot Memory Report";

    /// <summary>UTC timestamp when the report was generated.</summary>
    public string GeneratedAtUtc { get; init; } = string.Empty;

    /// <summary>Source directory scanned for database files.</summary>
    public string SourceDirectory { get; init; } = string.Empty;

    /// <summary>Snapshot rows grouped by session key, in chronological session order.</summary>
    public IReadOnlyList<MultiSnapshotSessionGroup> Sessions { get; init; } = [];
}

/// <summary>
/// A group of snapshots that share the same capture date, device/build context, and Unity/snap format.
/// </summary>
public sealed class MultiSnapshotSessionGroup
{
    /// <summary>Stable session identifier used for grouping.</summary>
    public string SessionKey { get; init; } = string.Empty;

    /// <summary>Human-readable session label shown in the report header.</summary>
    public string DisplayTitle { get; init; } = string.Empty;

    /// <summary>1-based session number (Memory Profiler style).</summary>
    public int SessionNumber { get; init; }

    /// <summary>Dominant platform for the session header icon.</summary>
    public CapturePlatformKind PlatformKind { get; init; }

    /// <summary>Snapshots in capture-time order within the session.</summary>
    public IReadOnlyList<SnapshotMetricsRow> Snapshots { get; init; } = [];
}

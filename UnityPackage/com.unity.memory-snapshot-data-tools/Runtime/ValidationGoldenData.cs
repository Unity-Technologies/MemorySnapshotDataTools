using System;

/// <summary>
/// Serializable golden reference values extracted from the Unity Memory Profiler for validation.
/// </summary>
[Serializable]
public sealed class GoldenSnapshot
{
    /// <summary>Base name of the snapshot file (no extension).</summary>
    public string SnapshotName;

    /// <summary>Full path to the source .snap file.</summary>
    public string SnapshotPath;

    /// <summary>Snapshot format version from metadata.</summary>
    public int FormatVersion;

    /// <summary>UTC timestamp when golden values were extracted.</summary>
    public string ExtractedAtUtc;

    /// <summary>
    /// Tracked metrics: <c>AssetBundle</c> (native objects) and <c>SerializedFile</c> (Unity Subsystems native roots).
    /// </summary>
    public NativeTypeMetric[] NativeTypeMetrics;

    /// <summary>Native root metrics for Remapper / PersistentManager rows.</summary>
    public NativeRootMetric[] NativeRootMetrics;

    /// <summary>Total allocated (committed) bytes from the Memory Profiler All Memory summary.</summary>
    public long TotalAllocatedBytes;

    /// <summary>Total resident bytes summed across the All Memory summary rows.</summary>
    public long TotalResidentBytes;

    /// <summary>
    /// Allocated memory distribution rows from the Memory Profiler All Memory Summary page
    /// (Native, Managed, Executables &amp; Mapped, Graphics, Untracked, and any platform-specific categories).
    /// </summary>
    public SummaryCategoryMetric[] AllocatedMemoryDistribution;

    /// <summary>
    /// Managed heap utilization rows from the Memory Profiler Managed Memory Summary page
    /// (Virtual Machine, Objects, Empty Heap Space).
    /// </summary>
    public SummaryCategoryMetric[] ManagedHeapUtilization;
}

/// <summary>
/// A single category row from a Memory Profiler Summary page, mirroring exactly what the UI shows.
/// </summary>
[Serializable]
public sealed class SummaryCategoryMetric
{
    /// <summary>Category label as shown in the Memory Profiler Summary page (trailing '*' markers removed).</summary>
    public string Name;

    /// <summary>Committed (allocated) bytes for the category.</summary>
    public long CommittedBytes;

    /// <summary>Resident bytes for the category (meaningful only when <see cref="ResidentAvailable"/> is true).</summary>
    public long ResidentBytes;

    /// <summary>True when resident size is available/meaningful for this category in the Memory Profiler UI.</summary>
    public bool ResidentAvailable;
}

/// <summary>
/// Aggregated allocated and resident memory for a tracked Memory Profiler breakdown row.
/// </summary>
[Serializable]
public sealed class NativeTypeMetric
{
    /// <summary>Metric label (e.g. AssetBundle native type or SerializedFile subsystem area).</summary>
    public string NativeTypeName;

    /// <summary>Number of native objects contributing to this metric.</summary>
    public int Count;

    /// <summary>Committed (allocated) bytes.</summary>
    public long AllocatedBytes;

    /// <summary>Resident bytes (0 when format does not provide page residency).</summary>
    public long ResidentBytes;
}

/// <summary>
/// Aggregated memory for a native root reference from Memory Profiler.
/// </summary>
[Serializable]
public sealed class NativeRootMetric
{
    /// <summary>Root area name.</summary>
    public string AreaName;

    /// <summary>Root object name.</summary>
    public string ObjectName;

    /// <summary>Committed (allocated) bytes from <see cref="ProcessedNativeRoots"/>.</summary>
    public long AllocatedBytes;

    /// <summary>Resident bytes from <see cref="ProcessedNativeRoots"/>.</summary>
    public long ResidentBytes;
}

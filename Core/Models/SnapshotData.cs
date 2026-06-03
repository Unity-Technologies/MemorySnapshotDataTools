namespace MemorySnapshotDataTools;

/// <summary>
/// In-memory container for all data extracted from a Unity memory snapshot (.snap).
/// Produced by <see cref="Parser.SnapshotBridge"/> and consumed by the export pipeline and validation.
/// </summary>
public sealed class RawSnapshotData
{
    /// <summary>Metadata about the snapshot (path, export time, Unity version).</summary>
    public SnapshotInfo SnapshotInfo { get; set; } = new();

    /// <summary>Native Unity objects (e.g. textures, GameObjects).</summary>
    public List<NativeObjectRow> NativeObjects { get; } = [];

    /// <summary>Managed heap objects.</summary>
    public List<ManagedObjectRow> ManagedObjects { get; } = [];

    /// <summary>Edges between objects (from_kind/from_index → to_kind/to_index).</summary>
    public List<ConnectionRow> Connections { get; } = [];

    /// <summary>Native root references (e.g. Scene, DontDestroyOnLoad) with accumulated sizes.</summary>
    public List<NativeRootRow> NativeRoots { get; } = [];

    /// <summary>Native memory regions (hierarchy and address ranges).</summary>
    public List<MemoryRegionRow> MemoryRegions { get; } = [];

    /// <summary>Allocations within native memory regions.</summary>
    public List<NativeAllocationRow> NativeAllocations { get; } = [];

    /// <summary>OS system memory regions (format v16+).</summary>
    public List<SystemMemoryRegionRow> SystemMemoryRegions { get; } = [];

    /// <summary>MemoryProfiler "Summary" page metrics (Allocated Memory Distribution + Managed Heap Utilization).</summary>
    public SummaryMetrics SummaryMetrics { get; set; } = new();

    /// <summary>Total number of data rows (all lists combined); used for pipeline progress.</summary>
    public long TotalRows => NativeObjects.Count + ManagedObjects.Count + Connections.Count
        + NativeRoots.Count + MemoryRegions.Count + NativeAllocations.Count + SystemMemoryRegions.Count;
}

/// <summary>
/// Metadata for a snapshot: path, when it was exported, and Unity version string.
/// Stored in the <c>snapshot_info</c> table and carried in <see cref="RawSnapshotData"/>.
/// </summary>
public sealed class SnapshotInfo
{
    /// <summary>Path to the source .snap file.</summary>
    public string SnapshotPath { get; set; } = string.Empty;

    /// <summary>When the snapshot was exported (UTC), as a string for display/storage.</summary>
    public string ExportedAtUtc { get; set; } = string.Empty;

    /// <summary>Unity version string from snapshot metadata, or format fallback.</summary>
    public string UnityVersion { get; set; } = string.Empty;

    /// <summary>Snap file format version (Metadata_Version).</summary>
    public uint SnapFormatVersion { get; set; }

    /// <summary>Profiler session GUID from the capture target.</summary>
    public uint SessionGuid { get; set; }

    /// <summary>Product or project name from the capture target.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Runtime platform name (e.g. IPhonePlayer).</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>Capture timestamp (UTC ISO-8601), when known.</summary>
    public string RecordDateUtc { get; set; } = string.Empty;

    /// <summary>
    /// OS memory page size in bytes for the captured device (from <c>SystemMemoryResidentPages_PageSize</c>;
    /// e.g. 16384 on iOS arm64, 4096 elsewhere). Zero when unknown (format &lt; 17 / no resident page data).
    /// </summary>
    public ulong PageSize { get; set; }
}

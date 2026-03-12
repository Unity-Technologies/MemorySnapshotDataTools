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

    /// <summary>Total number of data rows (all lists combined); used for pipeline progress.</summary>
    public long TotalRows => NativeObjects.Count + ManagedObjects.Count + Connections.Count
        + NativeRoots.Count + MemoryRegions.Count + NativeAllocations.Count;
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

    /// <summary>Unity version or format string from the snapshot.</summary>
    public string UnityVersion { get; set; } = string.Empty;
}

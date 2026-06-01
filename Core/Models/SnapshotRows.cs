namespace MemorySnapshotDataTools;

/// <summary>
/// One row from the <c>native_objects</c> table: a native Unity object (texture, GameObject, etc.).
/// </summary>
public struct NativeObjectRow
{
    /// <summary>Zero-based index in the native objects list.</summary>
    public int NativeObjectIndex;

    /// <summary>Instance ID string (e.g. from Unity).</summary>
    public string InstanceId;

    /// <summary>Display name.</summary>
    public string Name;

    /// <summary>Size in bytes (allocated / committed).</summary>
    public ulong SizeBytes;

    /// <summary>Native object base address from the snapshot.</summary>
    public ulong NativeObjectAddress;

    /// <summary>Native root reference id linking this object to <c>native_roots</c>, or -1 when unknown.</summary>
    public long RootReferenceId;

    /// <summary>
    /// Resident size in bytes for the object's native root (allocations and object ranges under the same
    /// <see cref="RootReferenceId"/>), matching Unity Memory Profiler processed-root totals; null when not
    /// computable (format &lt; 17) or when <see cref="RootReferenceId"/> is unknown.
    /// </summary>
    public ulong? ResidentSizeBytes;

    /// <summary>Index into the native type names array.</summary>
    public int TypeIndex;

    /// <summary>Resolved native type name (e.g. "Texture2D", "GameObject").</summary>
    public string NativeTypeName;

    /// <summary>Whether the object is marked destroyed.</summary>
    public bool IsDestroyed;
}

/// <summary>
/// One row from the <c>managed_objects</c> table: a managed heap object.
/// </summary>
public struct ManagedObjectRow
{
    /// <summary>Zero-based index in the managed objects list.</summary>
    public int ManagedObjectIndex;

    /// <summary>Address on the managed heap.</summary>
    public ulong Address;

    /// <summary>Size in bytes.</summary>
    public long SizeBytes;

    /// <summary>Index into the managed type descriptions.</summary>
    public int TypeIndex;

    /// <summary>Resolved managed type name.</summary>
    public string ManagedTypeName;

    /// <summary>Linked native object index, or -1 if none.</summary>
    public long NativeObjectIndex;
}

/// <summary>
/// One row from the <c>connections</c> table: an edge between two objects (e.g. reference, field).
/// </summary>
public struct ConnectionRow
{
    /// <summary>Source kind: "native_object" or "managed_object".</summary>
    public string FromKind;

    /// <summary>Source object index (native_object_index or managed_object_index).</summary>
    public long FromIndex;

    /// <summary>Target kind: "native_object" or "managed_object".</summary>
    public string ToKind;

    /// <summary>Target object index.</summary>
    public long ToIndex;

    /// <summary>Connection type label (e.g. "GCHandle", "Field").</summary>
    public string ConnectionType;
}

/// <summary>
/// One row from the <c>native_roots</c> table: a root reference (e.g. Scene, DontDestroyOnLoad) with accumulated size.
/// </summary>
public struct NativeRootRow
{
    /// <summary>Zero-based root index.</summary>
    public int RootIndex;

    /// <summary>Root ID from the snapshot.</summary>
    public long RootId;

    /// <summary>Area name (e.g. "Scene", "DontDestroyOnLoad").</summary>
    public string AreaName;

    /// <summary>Object name for the root.</summary>
    public string ObjectName;

    /// <summary>Accumulated size in bytes for this root (allocated).</summary>
    public ulong AccumulatedSizeBytes;

    /// <summary>Resident size in bytes aggregated from rooted objects and allocations, or null when not computable.</summary>
    public ulong? ResidentSizeBytes;
}

/// <summary>
/// One row from the <c>memory_regions</c> table: a native memory region (address range, hierarchy).
/// </summary>
public struct MemoryRegionRow
{
    /// <summary>Zero-based region index.</summary>
    public int RegionIndex;

    /// <summary>Base address of the region.</summary>
    public ulong AddressBase;

    /// <summary>Size of the region in bytes.</summary>
    public ulong AddressSize;

    /// <summary>Region name or label.</summary>
    public string Name;

    /// <summary>Parent region index, or -1 if none.</summary>
    public int ParentRegionIndex;

    /// <summary>Index of the first allocation in this region, or -1.</summary>
    public int FirstAllocationIndex;

    /// <summary>Number of allocations in this region.</summary>
    public int NumAllocations;
}

/// <summary>
/// One row from the <c>native_allocations</c> table: an allocation within a native memory region.
/// </summary>
public struct NativeAllocationRow
{
    /// <summary>Zero-based allocation index.</summary>
    public int AllocationIndex;

    /// <summary>Allocation address.</summary>
    public ulong Address;

    /// <summary>Size in bytes.</summary>
    public ulong SizeBytes;

    /// <summary>Overhead size in bytes.</summary>
    public ulong OverheadSizeBytes;

    /// <summary>Padding size in bytes.</summary>
    public ulong PaddingSizeBytes;

    /// <summary>Containing memory region index, or -1.</summary>
    public int MemoryRegionIndex;

    /// <summary>Root reference ID linking to <c>native_roots</c>, or -1.</summary>
    public long RootReferenceId;
}

/// <summary>
/// One row from the <c>system_memory_regions</c> table: an OS-level memory region with committed and resident totals.
/// </summary>
public struct SystemMemoryRegionRow
{
    /// <summary>Zero-based region index.</summary>
    public int RegionIndex;

    /// <summary>Region base address.</summary>
    public ulong Address;

    /// <summary>Committed (allocated) size in bytes for the region.</summary>
    public ulong SizeBytes;

    /// <summary>Resident size in bytes for the region.</summary>
    public ulong ResidentBytes;

    /// <summary>Region type code from the snapshot.</summary>
    public int Type;

    /// <summary>Region name.</summary>
    public string Name;
}

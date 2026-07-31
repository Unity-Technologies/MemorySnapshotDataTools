namespace MemorySnapshotDataTools.Parser;

/// <summary>Format of a snapshot entry: how element count and data are stored.</summary>
internal enum SnapEntryFormat : ushort
{
    /// <summary>Entry not present.</summary>
    Undefined = 0,

    /// <summary>Single value; size stored in metadata.</summary>
    SingleElement = 1,

    /// <summary>Fixed-size elements; count and element size in metadata.</summary>
    ConstantSizeElementArray = 2,

    /// <summary>Variable-length elements; offsets array defines boundaries.</summary>
    DynamicSizeElementArray = 3,
}

/// <summary>Identifiers for snapshot file sections (metadata, native types, connections, heap, etc.).</summary>
internal enum SnapEntryType : ushort
{
    Metadata_Version = 0,
    Metadata_RecordDate = 1,
    Metadata_UserMetadata = 2,
    Metadata_CaptureFlags = 3,
    Metadata_VirtualMachineInformation = 4,
    NativeTypes_Name = 5,
    NativeTypes_NativeBaseTypeArrayIndex = 6,
    NativeObjects_NativeTypeArrayIndex = 7,
    NativeObjects_HideFlags = 8,
    NativeObjects_Flags = 9,
    NativeObjects_InstanceId = 10,
    NativeObjects_Name = 11,
    NativeObjects_NativeObjectAddress = 12,
    NativeObjects_Size = 13,
    NativeObjects_RootReferenceId = 14,
    GCHandles_Target = 15,
    Connections_From = 16,
    Connections_To = 17,
    ManagedHeapSections_StartAddress = 18,
    ManagedHeapSections_Bytes = 19,
    TypeDescriptions_Flags = 22,
    TypeDescriptions_Name = 23,
    TypeDescriptions_Assembly = 24,
    TypeDescriptions_FieldIndices = 25,
    TypeDescriptions_StaticFieldBytes = 26,
    TypeDescriptions_BaseOrElementTypeIndex = 27,
    TypeDescriptions_Size = 28,
    TypeDescriptions_TypeInfoAddress = 29,
    FieldDescriptions_Offset = 31,
    FieldDescriptions_TypeIndex = 32,
    FieldDescriptions_Name = 33,
    FieldDescriptions_IsStatic = 34,
    NativeRootReferences_Id = 35,
    NativeRootReferences_AreaName = 36,
    NativeRootReferences_ObjectName = 37,
    NativeRootReferences_AccumulatedSize = 38,
    NativeAllocations_MemoryRegionIndex = 39,
    NativeAllocations_RootReferenceId = 40,
    NativeAllocations_Address = 42,
    NativeAllocations_Size = 43,
    NativeAllocations_OverheadSize = 44,
    NativeAllocations_PaddingSize = 45,
    NativeMemoryRegions_Name = 46,
    NativeMemoryRegions_ParentIndex = 47,
    NativeMemoryRegions_AddressBase = 48,
    NativeMemoryRegions_AddressSize = 49,
    NativeMemoryRegions_FirstAllocationIndex = 50,
    NativeMemoryRegions_NumAllocations = 51,
    NativeMemoryLabels_Name = 52,
    NativeObjects_GCHandleIndex = 58,
    NativeObjects_GCHandleIndex_Legacy = 62,
    ProfileTarget_Info = 59,
    ProfileTarget_MemoryStats = 60,
    SystemMemoryRegions_Address = 83,
    SystemMemoryRegions_Size = 84,
    SystemMemoryRegions_Resident = 85,
    SystemMemoryRegions_Type = 86,
    SystemMemoryRegions_Name = 87,
    SystemMemoryResidentPages_Address = 88,
    SystemMemoryResidentPages_FirstPageIndex = 89,
    SystemMemoryResidentPages_LastPageIndex = 90,
    SystemMemoryResidentPages_PagesState = 91,
    SystemMemoryResidentPages_PageSize = 92,

    // Optional swapped-page entries appended at format v17 (the version stays 17; gate on entry
    // presence, never on version — v18 is EntityIDAs8ByteStructs). Same encoding as entries 88–92.
    SystemMemorySwappedPages_Address = 93,
    SystemMemorySwappedPages_FirstPageIndex = 94,
    SystemMemorySwappedPages_LastPageIndex = 95,
    SystemMemorySwappedPages_PagesState = 96,
    SystemMemorySwappedPages_PageSize = 97,
}

/// <summary>Format version constants used when decoding snapshot entries (e.g. instance IDs, heap sections).</summary>
internal static class SnapFormatVersion
{
    /// <summary>Version at which native connections use instance IDs.</summary>
    public const uint NativeConnectionsAsInstanceIdsVersion = 10;

    /// <summary>Version at which entity IDs are 8-byte structs.</summary>
    public const uint EntityIDAs8ByteStructs = 18;

    /// <summary>Version for memory label size and heap ID in heap section metadata.</summary>
    public const uint MemLabelSizeAndHeapIdVersion = 12;

    /// <summary>Version at which OS system memory regions are present (entries 83–87).</summary>
    public const uint SystemMemoryRegionsVersion = 16;

    /// <summary>Version at which per-page resident bitmaps are present (entries 88–92).</summary>
    public const uint SystemMemoryResidentPagesVersion = 17;
}

/// <summary>
/// Type of a managed heap section, decoded from the high bit of its start address.
/// Mirrors Unity Memory Profiler's <c>ManagedMemorySectionEntriesCache.MemorySectionType</c>.
/// </summary>
public enum ManagedHeapSectionKind : byte
{
    /// <summary>Garbage collector heap section (reported as "Empty Heap Space" in the summary).</summary>
    GarbageCollector = 0,

    /// <summary>Virtual machine heap section (reported as "Virtual Machine" in the summary).</summary>
    VirtualMachine = 1,
}

/// <summary>
/// Process-wide memory statistics reported by the profiling target (<c>ProfileTarget_MemoryStats</c>).
/// Used by the summary builder for graphics estimation and the legacy untracked fallback.
/// </summary>
public sealed class DecodedTargetMemoryStats
{
    /// <summary>Total virtual memory committed by the process, as reported by the OS.</summary>
    public ulong TotalVirtualMemory { get; set; }

    /// <summary>Estimated graphics memory used, as reported by the graphics driver.</summary>
    public ulong GraphicsUsedMemory { get; set; }
}

/// <summary>
/// Decoded virtual machine layout from snapshot metadata (pointer size, header layout, allocation granularity).
/// Used by <see cref="ManagedSnapshotCrawler"/> to interpret managed heap layout.
/// </summary>
public sealed class DecodedVirtualMachineInfo
{
    /// <summary>Size of a pointer in bytes (4 or 8).</summary>
    public uint PointerSize { get; set; }

    /// <summary>Object header size in bytes.</summary>
    public uint ObjectHeaderSize { get; set; }

    /// <summary>Array object header size in bytes.</summary>
    public uint ArrayHeaderSize { get; set; }

    /// <summary>Offset of array bounds in the array header.</summary>
    public uint ArrayBoundsOffsetInHeader { get; set; }

    /// <summary>Offset of array length/size in the array header.</summary>
    public uint ArraySizeOffsetInHeader { get; set; }

    /// <summary>Allocation granularity in bytes.</summary>
    public uint AllocationGranularity { get; set; }
}

/// <summary>
/// Fully decoded in-memory snapshot: all native and managed metadata and raw arrays as read from the .snap file.
/// Produced by <see cref="SnapSectionDecoders.DecodeAll"/> and consumed by <see cref="SnapshotBridge.ExtractFromDecoded"/> and <see cref="ManagedSnapshotCrawler"/>.
/// </summary>
public sealed class DecodedSnapshot
{
    /// <summary>Snapshot format version from metadata.</summary>
    public uint FormatVersion { get; set; }

    /// <summary>Record date in .NET ticks (UTC).</summary>
    public long RecordDateTicksUtc { get; set; }

    /// <summary>Session, platform, and Unity version from snapshot metadata entries.</summary>
    public CaptureMetadata CaptureMetadata { get; set; } = new();

    /// <summary>Native type display names.</summary>
    public string[] NativeTypeNames { get; set; } = [];

    /// <summary>Per-native-object index into <see cref="NativeTypeNames"/>.</summary>
    public int[] NativeObjectTypeIndices { get; set; } = [];

    /// <summary>Per-native-object instance ID.</summary>
    public ulong[] NativeObjectInstanceIds { get; set; } = [];

    /// <summary>Per-native-object name.</summary>
    public string[] NativeObjectNames { get; set; } = [];

    /// <summary>Per-native-object size in bytes.</summary>
    public ulong[] NativeObjectSizes { get; set; } = [];

    /// <summary>Per-native-object base address, or 0 when not present in the capture.</summary>
    public ulong[] NativeObjectAddresses { get; set; } = [];

    /// <summary>Per-native-object root reference ID linking to <see cref="NativeRootIds"/>, or -1.</summary>
    public long[] NativeObjectRootReferenceIds { get; set; } = [];

    /// <summary>Per-native-object flags (e.g. destroyed).</summary>
    public int[] NativeObjectFlags { get; set; } = [];

    /// <summary>Per-native-object GC handle index, or -1.</summary>
    public int[] NativeObjectGcHandleIndices { get; set; } = [];

    /// <summary>GC handle target addresses (managed heap).</summary>
    public ulong[] GcHandleTargets { get; set; } = [];

    /// <summary>Connection source unified indices.</summary>
    public int[] ConnectionsFrom { get; set; } = [];

    /// <summary>Connection target unified indices.</summary>
    public int[] ConnectionsTo { get; set; } = [];

    /// <summary>Native root reference IDs.</summary>
    public long[] NativeRootIds { get; set; } = [];

    /// <summary>Native root area names (e.g. Scene, DontDestroyOnLoad).</summary>
    public string[] NativeRootAreaNames { get; set; } = [];

    /// <summary>Native root object names.</summary>
    public string[] NativeRootObjectNames { get; set; } = [];

    /// <summary>Native root accumulated sizes in bytes.</summary>
    public ulong[] NativeRootAccumulatedSizes { get; set; } = [];

    /// <summary>Native memory region names.</summary>
    public string[] NativeMemoryRegionNames { get; set; } = [];

    /// <summary>Parent region index per region, or -1.</summary>
    public int[] NativeMemoryRegionParentIndices { get; set; } = [];

    /// <summary>Base address per region.</summary>
    public ulong[] NativeMemoryRegionAddressBases { get; set; } = [];

    /// <summary>Size in bytes per region.</summary>
    public ulong[] NativeMemoryRegionAddressSizes { get; set; } = [];

    /// <summary>First allocation index per region, or -1.</summary>
    public int[] NativeMemoryRegionFirstAllocationIndices { get; set; } = [];

    /// <summary>Number of allocations per region.</summary>
    public int[] NativeMemoryRegionNumAllocations { get; set; } = [];

    /// <summary>Native memory label names.</summary>
    public string[] NativeMemoryLabelNames { get; set; } = [];

    /// <summary>Native allocation addresses.</summary>
    public ulong[] NativeAllocationAddresses { get; set; } = [];

    /// <summary>Native allocation sizes in bytes.</summary>
    public ulong[] NativeAllocationSizes { get; set; } = [];

    /// <summary>Native allocation overhead sizes in bytes.</summary>
    public ulong[] NativeAllocationOverheadSizes { get; set; } = [];

    /// <summary>Native allocation padding sizes in bytes.</summary>
    public ulong[] NativeAllocationPaddingSizes { get; set; } = [];

    /// <summary>Memory region index per allocation, or -1.</summary>
    public int[] NativeAllocationMemoryRegionIndices { get; set; } = [];

    /// <summary>Root reference ID per allocation, or -1.</summary>
    public long[] NativeAllocationRootReferenceIds { get; set; } = [];

    /// <summary>OS system memory region base addresses (format v16+).</summary>
    public ulong[] SystemMemoryRegionAddresses { get; set; } = [];

    /// <summary>OS system memory region committed sizes in bytes (format v16+).</summary>
    public ulong[] SystemMemoryRegionSizes { get; set; } = [];

    /// <summary>OS system memory region resident sizes in bytes (format v16+).</summary>
    public ulong[] SystemMemoryRegionResidentSizes { get; set; } = [];

    /// <summary>OS system memory region type codes (format v16+).</summary>
    public int[] SystemMemoryRegionTypes { get; set; } = [];

    /// <summary>OS system memory region names (format v16+).</summary>
    public string[] SystemMemoryRegionNames { get; set; } = [];

    /// <summary>Resident-page range base addresses, one per system region (format v17+).</summary>
    public ulong[] SystemMemoryResidentPageAddresses { get; set; } = [];

    /// <summary>First page index in the global page bitmap per system region (format v17+).</summary>
    public int[] SystemMemoryResidentPageFirstIndices { get; set; } = [];

    /// <summary>Last page index in the global page bitmap per system region (format v17+).</summary>
    public int[] SystemMemoryResidentPageLastIndices { get; set; } = [];

    /// <summary>Global page residency bitmap bytes (format v17+); one element holds the full bitset.</summary>
    public byte[][] SystemMemoryResidentPageStates { get; set; } = [];

    /// <summary>Page size in bytes for resident page calculations (format v17+).</summary>
    public ulong SystemMemoryResidentPageSize { get; set; }

    /// <summary>Swapped-page range base addresses, one per system region (optional v17+ entries 93–97; empty when absent).</summary>
    public ulong[] SystemMemorySwappedPageAddresses { get; set; } = [];

    /// <summary>First page index in the global swapped-page bitmap per system region (optional v17+).</summary>
    public int[] SystemMemorySwappedPageFirstIndices { get; set; } = [];

    /// <summary>Last page index in the global swapped-page bitmap per system region (optional v17+).</summary>
    public int[] SystemMemorySwappedPageLastIndices { get; set; } = [];

    /// <summary>Global page swapped bitmap bytes (optional v17+); one element holds the full bitset (bit i = page i swapped, LSB-first).</summary>
    public byte[][] SystemMemorySwappedPageStates { get; set; } = [];

    /// <summary>Page size in bytes for swapped page calculations (optional v17+); raw byte count, not an exponent.</summary>
    public ulong SystemMemorySwappedPageSize { get; set; }

    /// <summary>VM layout (pointer size, header offsets).</summary>
    public DecodedVirtualMachineInfo VirtualMachineInformation { get; set; } = new();

    /// <summary>Start address of each managed heap section (high type-bit masked off).</summary>
    public ulong[] ManagedHeapSectionStartAddresses { get; set; } = [];

    /// <summary>Type (VM vs GC) of each managed heap section, parallel to <see cref="ManagedHeapSectionStartAddresses"/>.</summary>
    public ManagedHeapSectionKind[] ManagedHeapSectionTypes { get; set; } = [];

    /// <summary>Raw bytes of each managed heap section.</summary>
    public byte[][] ManagedHeapSectionBytes { get; set; } = [];

    /// <summary>Process memory statistics from the capture target, or null when absent.</summary>
    public DecodedTargetMemoryStats? TargetMemoryStats { get; set; }

    /// <summary>Managed type flags (value type, array, etc.).</summary>
    public int[] ManagedTypeFlags { get; set; } = [];

    /// <summary>Managed type names.</summary>
    public string[] ManagedTypeNames { get; set; } = [];

    /// <summary>Managed type assembly names.</summary>
    public string[] ManagedTypeAssemblies { get; set; } = [];

    /// <summary>Base or element type index per managed type.</summary>
    public int[] ManagedTypeBaseOrElementTypeIndices { get; set; } = [];

    /// <summary>Managed type size in bytes.</summary>
    public int[] ManagedTypeSizes { get; set; } = [];

    /// <summary>Type info address per managed type (for type resolution on heap).</summary>
    public ulong[] ManagedTypeInfoAddresses { get; set; } = [];

    /// <summary>Per-type array of field description indices.</summary>
    public int[][] ManagedTypeFieldIndices { get; set; } = [];

    /// <summary>Per-type static field byte blob (static field values), parallel to <see cref="ManagedTypeNames"/>; empty when absent.</summary>
    public byte[][] ManagedTypeStaticFieldBytes { get; set; } = [];

    /// <summary>Field offset in bytes.</summary>
    public int[] FieldOffsets { get; set; } = [];

    /// <summary>Field type index.</summary>
    public int[] FieldTypeIndices { get; set; } = [];

    /// <summary>Field name.</summary>
    public string[] FieldNames { get; set; } = [];

    /// <summary>Non-zero if field is static.</summary>
    public byte[] FieldIsStatic { get; set; } = [];
}


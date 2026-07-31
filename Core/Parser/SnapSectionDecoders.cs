namespace MemorySnapshotDataTools.Parser;

/// <summary>
/// Decodes all snapshot sections from a <see cref="SnapReader"/> into a single <see cref="DecodedSnapshot"/>.
/// Reads metadata, native types/objects, connections, roots, memory regions, allocations, managed heap sections, type descriptions, and fields.
/// Validates array length consistency before returning.
/// </summary>
internal static class SnapSectionDecoders
{
    private const ulong HeapSectionTypeFlagMask = 1UL << 63;

    /// <summary>
    /// Reads every required and optional entry from the snapshot and populates a <see cref="DecodedSnapshot"/>.
    /// </summary>
    /// <param name="reader">Open snapshot reader (e.g. from <see cref="SnapReader.Open"/>).</param>
    /// <returns>A fully populated decoded snapshot; throws if required entries are missing or lengths are inconsistent.</returns>
    /// <exception cref="InvalidOperationException">If a required entry is missing or array lengths do not match.</exception>
    public static DecodedSnapshot DecodeAll(SnapReader reader)
    {
        var formatVersion = reader.ReadMetadataVersion();
        var captureMetadata = SnapMetadataReader.Read(reader);
        var nativeObjectTypeIndices = ReadInts(reader, SnapEntryType.NativeObjects_NativeTypeArrayIndex);
        var nativeObjectCount = nativeObjectTypeIndices.Length;
        var nativeObjectInstanceIds = ReadInstanceIds(reader, formatVersion);
        var nativeObjectGcHandleIndices = ReadNativeObjectGcHandleIndices(reader, formatVersion, nativeObjectCount);
        var gcHandleTargets = ReadOptionalULongs(reader, SnapEntryType.GCHandles_Target);
        var (connectionsFrom, connectionsTo) = ReadConnections(
            reader,
            formatVersion,
            nativeObjectInstanceIds,
            nativeObjectGcHandleIndices,
            gcHandleTargets.Length);
        var nativeMemoryRegionAddressBases = ReadULongs(reader, SnapEntryType.NativeMemoryRegions_AddressBase);
        var nativeMemoryRegionCount = nativeMemoryRegionAddressBases.Length;
        var nativeAllocationAddresses = ReadULongs(reader, SnapEntryType.NativeAllocations_Address);
        var nativeAllocationCount = nativeAllocationAddresses.Length;

        var snapshot = new DecodedSnapshot
        {
            FormatVersion = formatVersion,
            RecordDateTicksUtc = reader.ReadMetadataRecordDateTicks(),
            CaptureMetadata = captureMetadata,
            NativeObjectTypeIndices = nativeObjectTypeIndices,
            NativeObjectInstanceIds = nativeObjectInstanceIds,
            NativeObjectSizes = ReadULongs(reader, SnapEntryType.NativeObjects_Size),
            NativeObjectAddresses = ReadULongsWithCount(reader, SnapEntryType.NativeObjects_NativeObjectAddress, nativeObjectCount),
            NativeObjectRootReferenceIds = ReadLongsWithCount(reader, SnapEntryType.NativeObjects_RootReferenceId, nativeObjectCount, -1),
            NativeObjectFlags = ReadIntsWithCount(reader, SnapEntryType.NativeObjects_Flags, nativeObjectCount, 0),
            NativeObjectGcHandleIndices = nativeObjectGcHandleIndices,
            GcHandleTargets = gcHandleTargets,
            ConnectionsFrom = connectionsFrom,
            ConnectionsTo = connectionsTo,
            NativeRootIds = ReadLongs(reader, SnapEntryType.NativeRootReferences_Id),
            NativeRootAccumulatedSizes = ReadULongs(reader, SnapEntryType.NativeRootReferences_AccumulatedSize),
            NativeMemoryRegionAddressBases = nativeMemoryRegionAddressBases,
            NativeMemoryRegionAddressSizes = ReadULongsWithCount(reader, SnapEntryType.NativeMemoryRegions_AddressSize, nativeMemoryRegionCount),
            NativeMemoryRegionParentIndices = ReadIntsWithCount(reader, SnapEntryType.NativeMemoryRegions_ParentIndex, nativeMemoryRegionCount, -1),
            NativeMemoryRegionFirstAllocationIndices = ReadIntsWithCount(reader, SnapEntryType.NativeMemoryRegions_FirstAllocationIndex, nativeMemoryRegionCount, -1),
            NativeMemoryRegionNumAllocations = ReadIntsWithCount(reader, SnapEntryType.NativeMemoryRegions_NumAllocations, nativeMemoryRegionCount, 0),
            NativeAllocationAddresses = nativeAllocationAddresses,
            NativeAllocationSizes = ReadULongsWithCount(reader, SnapEntryType.NativeAllocations_Size, nativeAllocationCount),
            NativeAllocationOverheadSizes = ReadULongsWithCount(reader, SnapEntryType.NativeAllocations_OverheadSize, nativeAllocationCount),
            NativeAllocationPaddingSizes = ReadULongsWithCount(reader, SnapEntryType.NativeAllocations_PaddingSize, nativeAllocationCount),
            NativeAllocationMemoryRegionIndices = ReadIntsWithCount(reader, SnapEntryType.NativeAllocations_MemoryRegionIndex, nativeAllocationCount, -1),
            NativeAllocationRootReferenceIds = ReadLongsWithCount(reader, SnapEntryType.NativeAllocations_RootReferenceId, nativeAllocationCount, -1),
            VirtualMachineInformation = ReadVirtualMachineInfo(reader),
            ManagedHeapSectionStartAddresses = ReadManagedHeapSectionStartAddresses(reader, formatVersion),
            ManagedHeapSectionTypes = ReadManagedHeapSectionTypes(reader, formatVersion),
            ManagedHeapSectionBytes = ReadRequiredDynamicBytes(reader, SnapEntryType.ManagedHeapSections_Bytes),
            TargetMemoryStats = ReadTargetMemoryStats(reader),
            ManagedTypeFlags = ReadRequiredInts(reader, SnapEntryType.TypeDescriptions_Flags),
            ManagedTypeNames = ReadRequiredStrings(reader, SnapEntryType.TypeDescriptions_Name),
            ManagedTypeAssemblies = ReadRequiredStrings(reader, SnapEntryType.TypeDescriptions_Assembly),
            ManagedTypeFieldIndices = ReadRequiredDynamicInts(reader, SnapEntryType.TypeDescriptions_FieldIndices),
            ManagedTypeStaticFieldBytes = ReadOptionalDynamicBytes(reader, SnapEntryType.TypeDescriptions_StaticFieldBytes),
            ManagedTypeBaseOrElementTypeIndices = ReadRequiredInts(reader, SnapEntryType.TypeDescriptions_BaseOrElementTypeIndex),
            ManagedTypeSizes = ReadRequiredInts(reader, SnapEntryType.TypeDescriptions_Size),
            ManagedTypeInfoAddresses = ReadRequiredULongs(reader, SnapEntryType.TypeDescriptions_TypeInfoAddress),
            FieldOffsets = ReadRequiredInts(reader, SnapEntryType.FieldDescriptions_Offset),
            FieldTypeIndices = ReadRequiredInts(reader, SnapEntryType.FieldDescriptions_TypeIndex),
            FieldNames = ReadRequiredStrings(reader, SnapEntryType.FieldDescriptions_Name),
            FieldIsStatic = ReadRequiredBytes(reader, SnapEntryType.FieldDescriptions_IsStatic),
        };

        snapshot.NativeTypeNames = ReadStringsWithCount(reader, SnapEntryType.NativeTypes_Name, 0);
        snapshot.NativeObjectNames = ReadStringsWithCount(reader, SnapEntryType.NativeObjects_Name, snapshot.NativeObjectTypeIndices.Length);
        snapshot.NativeRootAreaNames = ReadStringsWithCount(reader, SnapEntryType.NativeRootReferences_AreaName, snapshot.NativeRootIds.Length);
        snapshot.NativeRootObjectNames = ReadStringsWithCount(reader, SnapEntryType.NativeRootReferences_ObjectName, snapshot.NativeRootIds.Length);
        snapshot.NativeMemoryRegionNames = ReadStringsWithCount(reader, SnapEntryType.NativeMemoryRegions_Name, snapshot.NativeMemoryRegionAddressBases.Length);
        snapshot.NativeMemoryLabelNames = ReadStrings(reader, SnapEntryType.NativeMemoryLabels_Name);

        if (formatVersion >= SnapFormatVersion.SystemMemoryRegionsVersion)
        {
            snapshot.SystemMemoryRegionAddresses = ReadOptionalULongs(reader, SnapEntryType.SystemMemoryRegions_Address);
            var systemRegionCount = snapshot.SystemMemoryRegionAddresses.Length;
            snapshot.SystemMemoryRegionSizes = ReadULongsWithCount(reader, SnapEntryType.SystemMemoryRegions_Size, systemRegionCount);
            snapshot.SystemMemoryRegionResidentSizes = ReadULongsWithCount(reader, SnapEntryType.SystemMemoryRegions_Resident, systemRegionCount);
            snapshot.SystemMemoryRegionTypes = ReadSystemMemoryRegionTypes(reader, systemRegionCount);
            snapshot.SystemMemoryRegionNames = ReadStringsWithCount(reader, SnapEntryType.SystemMemoryRegions_Name, systemRegionCount);
        }

        if (formatVersion >= SnapFormatVersion.SystemMemoryResidentPagesVersion)
        {
            snapshot.SystemMemoryResidentPageAddresses = ReadOptionalULongs(reader, SnapEntryType.SystemMemoryResidentPages_Address);
            var residentPageRangeCount = snapshot.SystemMemoryResidentPageAddresses.Length;
            snapshot.SystemMemoryResidentPageFirstIndices = ReadIntsWithCount(reader, SnapEntryType.SystemMemoryResidentPages_FirstPageIndex, residentPageRangeCount, 0);
            snapshot.SystemMemoryResidentPageLastIndices = ReadIntsWithCount(reader, SnapEntryType.SystemMemoryResidentPages_LastPageIndex, residentPageRangeCount, 0);
            snapshot.SystemMemoryResidentPageStates = ReadPageStates(reader, SnapEntryType.SystemMemoryResidentPages_PagesState);
            snapshot.SystemMemoryResidentPageSize = ReadPageSize(reader, SnapEntryType.SystemMemoryResidentPages_PageSize);

            // Swapped-page entries (93–97) are optional additions at v17. The format version stays 17,
            // so gate on entry presence only — the ReadOptional* helpers return empty when absent.
            snapshot.SystemMemorySwappedPageAddresses = ReadOptionalULongs(reader, SnapEntryType.SystemMemorySwappedPages_Address);
            var swappedPageRangeCount = snapshot.SystemMemorySwappedPageAddresses.Length;
            snapshot.SystemMemorySwappedPageFirstIndices = ReadIntsWithCount(reader, SnapEntryType.SystemMemorySwappedPages_FirstPageIndex, swappedPageRangeCount, 0);
            snapshot.SystemMemorySwappedPageLastIndices = ReadIntsWithCount(reader, SnapEntryType.SystemMemorySwappedPages_LastPageIndex, swappedPageRangeCount, 0);
            snapshot.SystemMemorySwappedPageStates = ReadPageStates(reader, SnapEntryType.SystemMemorySwappedPages_PagesState);
            snapshot.SystemMemorySwappedPageSize = ReadPageSize(reader, SnapEntryType.SystemMemorySwappedPages_PageSize);
        }

        ValidateLengths(snapshot);
        return snapshot;
    }

    private static void ValidateLengths(DecodedSnapshot snapshot)
    {
        var nativeCount = snapshot.NativeObjectNames.Length;
        if (nativeCount > 0)
        {
            EnsureArrayLength(nativeCount, snapshot.NativeObjectTypeIndices.Length, "NativeObjects_NativeTypeArrayIndex");
            EnsureArrayLength(nativeCount, snapshot.NativeObjectInstanceIds.Length, "NativeObjects_InstanceId");
            EnsureArrayLength(nativeCount, snapshot.NativeObjectSizes.Length, "NativeObjects_Size");
            EnsureArrayLength(nativeCount, snapshot.NativeObjectGcHandleIndices.Length, "NativeObjects_GCHandleIndex");
            if (snapshot.NativeObjectFlags.Length > 0)
                EnsureArrayLength(nativeCount, snapshot.NativeObjectFlags.Length, "NativeObjects_Flags");
            if (snapshot.NativeObjectAddresses.Length > 0)
                EnsureArrayLength(nativeCount, snapshot.NativeObjectAddresses.Length, "NativeObjects_NativeObjectAddress");
            if (snapshot.NativeObjectRootReferenceIds.Length > 0)
                EnsureArrayLength(nativeCount, snapshot.NativeObjectRootReferenceIds.Length, "NativeObjects_RootReferenceId");
        }

        var rootsCount = snapshot.NativeRootIds.Length;
        if (snapshot.NativeRootAreaNames.Length > 0)
            EnsureArrayLength(rootsCount, snapshot.NativeRootAreaNames.Length, "NativeRootReferences_AreaName");
        if (snapshot.NativeRootObjectNames.Length > 0)
            EnsureArrayLength(rootsCount, snapshot.NativeRootObjectNames.Length, "NativeRootReferences_ObjectName");
        EnsureArrayLength(rootsCount, snapshot.NativeRootAccumulatedSizes.Length, "NativeRootReferences_AccumulatedSize");

        EnsureArrayLength(snapshot.ConnectionsFrom.Length, snapshot.ConnectionsTo.Length, "Connections_To");

        var regionCount = snapshot.NativeMemoryRegionAddressBases.Length;
        EnsureArrayLength(regionCount, snapshot.NativeMemoryRegionAddressSizes.Length, "NativeMemoryRegions_AddressSize");
        EnsureArrayLength(regionCount, snapshot.NativeMemoryRegionParentIndices.Length, "NativeMemoryRegions_ParentIndex");
        EnsureArrayLength(regionCount, snapshot.NativeMemoryRegionFirstAllocationIndices.Length, "NativeMemoryRegions_FirstAllocationIndex");
        EnsureArrayLength(regionCount, snapshot.NativeMemoryRegionNumAllocations.Length, "NativeMemoryRegions_NumAllocations");
        if (snapshot.NativeMemoryRegionNames.Length > 0)
            EnsureArrayLength(regionCount, snapshot.NativeMemoryRegionNames.Length, "NativeMemoryRegions_Name");

        var allocationCount = snapshot.NativeAllocationAddresses.Length;
        EnsureArrayLength(allocationCount, snapshot.NativeAllocationSizes.Length, "NativeAllocations_Size");
        EnsureArrayLength(allocationCount, snapshot.NativeAllocationOverheadSizes.Length, "NativeAllocations_OverheadSize");
        EnsureArrayLength(allocationCount, snapshot.NativeAllocationPaddingSizes.Length, "NativeAllocations_PaddingSize");
        EnsureArrayLength(allocationCount, snapshot.NativeAllocationMemoryRegionIndices.Length, "NativeAllocations_MemoryRegionIndex");
        if (snapshot.NativeAllocationRootReferenceIds.Length > 0)
            EnsureArrayLength(allocationCount, snapshot.NativeAllocationRootReferenceIds.Length, "NativeAllocations_RootReferenceId");

        var systemRegionCount = snapshot.SystemMemoryRegionAddresses.Length;
        if (systemRegionCount > 0)
        {
            EnsureArrayLength(systemRegionCount, snapshot.SystemMemoryRegionSizes.Length, "SystemMemoryRegions_Size");
            EnsureArrayLength(systemRegionCount, snapshot.SystemMemoryRegionResidentSizes.Length, "SystemMemoryRegions_Resident");
            if (snapshot.SystemMemoryRegionTypes.Length > 0)
                EnsureArrayLength(systemRegionCount, snapshot.SystemMemoryRegionTypes.Length, "SystemMemoryRegions_Type");
            if (snapshot.SystemMemoryRegionNames.Length > 0)
                EnsureArrayLength(systemRegionCount, snapshot.SystemMemoryRegionNames.Length, "SystemMemoryRegions_Name");
        }

        var residentPageCount = snapshot.SystemMemoryResidentPageAddresses.Length;
        if (residentPageCount > 0)
        {
            EnsureArrayLength(residentPageCount, snapshot.SystemMemoryResidentPageFirstIndices.Length, "SystemMemoryResidentPages_FirstPageIndex");
            EnsureArrayLength(residentPageCount, snapshot.SystemMemoryResidentPageLastIndices.Length, "SystemMemoryResidentPages_LastPageIndex");
        }

        var swappedPageCount = snapshot.SystemMemorySwappedPageAddresses.Length;
        if (swappedPageCount > 0)
        {
            EnsureArrayLength(swappedPageCount, snapshot.SystemMemorySwappedPageFirstIndices.Length, "SystemMemorySwappedPages_FirstPageIndex");
            EnsureArrayLength(swappedPageCount, snapshot.SystemMemorySwappedPageLastIndices.Length, "SystemMemorySwappedPages_LastPageIndex");
        }

        EnsureArrayLength(snapshot.ManagedHeapSectionStartAddresses.Length, snapshot.ManagedHeapSectionBytes.Length, "ManagedHeapSections_Bytes");
        EnsureArrayLength(snapshot.ManagedHeapSectionStartAddresses.Length, snapshot.ManagedHeapSectionTypes.Length, "ManagedHeapSections_Type");

        var managedTypeCount = snapshot.ManagedTypeNames.Length;
        EnsureArrayLength(managedTypeCount, snapshot.ManagedTypeFlags.Length, "TypeDescriptions_Flags");
        EnsureArrayLength(managedTypeCount, snapshot.ManagedTypeAssemblies.Length, "TypeDescriptions_Assembly");
        EnsureArrayLength(managedTypeCount, snapshot.ManagedTypeFieldIndices.Length, "TypeDescriptions_FieldIndices");
        EnsureArrayLength(managedTypeCount, snapshot.ManagedTypeBaseOrElementTypeIndices.Length, "TypeDescriptions_BaseOrElementTypeIndex");
        EnsureArrayLength(managedTypeCount, snapshot.ManagedTypeSizes.Length, "TypeDescriptions_Size");
        EnsureArrayLength(managedTypeCount, snapshot.ManagedTypeInfoAddresses.Length, "TypeDescriptions_TypeInfoAddress");

        var fieldCount = snapshot.FieldNames.Length;
        EnsureArrayLength(fieldCount, snapshot.FieldOffsets.Length, "FieldDescriptions_Offset");
        EnsureArrayLength(fieldCount, snapshot.FieldTypeIndices.Length, "FieldDescriptions_TypeIndex");
        EnsureArrayLength(fieldCount, snapshot.FieldIsStatic.Length, "FieldDescriptions_IsStatic");
    }

    private static void EnsureArrayLength(int expected, int actual, string name)
    {
        if (expected != actual)
            throw new InvalidOperationException($"Array length mismatch for {name}. expected={expected}, actual={actual}");
    }

    private static string[] ReadStrings(SnapReader reader, SnapEntryType type)
        => reader.HasEntry(type) ? reader.ReadUtf8StringArray(type) : [];

    private static string[] ReadStringsWithCount(SnapReader reader, SnapEntryType type, int fallbackCount)
    {
        if (!reader.HasEntry(type))
            return fallbackCount > 0 ? Enumerable.Repeat(string.Empty, fallbackCount).ToArray() : [];

        try
        {
            return reader.ReadUtf8StringArray(type);
        }
        catch
        {
            var count = fallbackCount;
            if (count <= 0)
            {
                try
                {
                    count = checked((int)reader.GetEntryCount(type));
                }
                catch
                {
                    count = 0;
                }
            }

            return count > 0 ? Enumerable.Repeat(string.Empty, count).ToArray() : [];
        }
    }

    private static int[] ReadInts(SnapReader reader, SnapEntryType type)
        => reader.HasEntry(type) ? reader.ReadPrimitiveArray<int>(type) : [];

    private static int[] ReadRequiredInts(SnapReader reader, SnapEntryType type)
    {
        EnsureEntryExists(reader, type);
        return reader.ReadPrimitiveArray<int>(type);
    }

    /// <summary>
    /// Reads <c>SystemMemoryRegions_Type</c> as an int array. The values are serialized as <c>ushort</c>
    /// (Unity's <c>MemoryType : ushort</c>); falls back to int then byte element sizes for other formats.
    /// Reading with the wrong element size throws on a byte-size mismatch, which previously left every
    /// region misclassified as Private/Untracked.
    /// </summary>
    private static int[] ReadSystemMemoryRegionTypes(SnapReader reader, int count)
    {
        if (!reader.HasEntry(SnapEntryType.SystemMemoryRegions_Type))
            return count > 0 ? new int[count] : [];

        try
        {
            var ushorts = reader.ReadPrimitiveArray<ushort>(SnapEntryType.SystemMemoryRegions_Type);
            if (ushorts.Length == count)
                return Array.ConvertAll(ushorts, v => (int)v);
        }
        catch
        {
            // Try other element widths below.
        }

        var ints = ReadOptionalInts(reader, SnapEntryType.SystemMemoryRegions_Type);
        if (ints.Length == count)
            return ints;

        try
        {
            var bytes = reader.ReadPrimitiveArray<byte>(SnapEntryType.SystemMemoryRegions_Type);
            if (bytes.Length == count)
                return Array.ConvertAll(bytes, v => (int)v);
        }
        catch
        {
            // Fall through to zero-filled fallback.
        }

        return count > 0 ? new int[count] : [];
    }

    private static int[] ReadIntsWithCount(SnapReader reader, SnapEntryType type, int fallbackCount, int fallbackValue = 0)
    {
        var values = ReadOptionalInts(reader, type);
        if (values.Length > 0)
            return values;

        return fallbackCount > 0 ? Enumerable.Repeat(fallbackValue, fallbackCount).ToArray() : [];
    }

    private static long[] ReadRequiredLongs(SnapReader reader, SnapEntryType type)
    {
        EnsureEntryExists(reader, type);
        return reader.ReadPrimitiveArray<long>(type);
    }

    private static ulong[] ReadRequiredULongs(SnapReader reader, SnapEntryType type)
    {
        EnsureEntryExists(reader, type);
        return reader.ReadPrimitiveArray<ulong>(type);
    }

    private static ulong[] ReadULongsWithCount(SnapReader reader, SnapEntryType type, int fallbackCount)
    {
        var values = ReadOptionalULongs(reader, type);
        if (values.Length > 0)
            return values;

        return fallbackCount > 0 ? new ulong[fallbackCount] : [];
    }

    private static byte[] ReadRequiredBytes(SnapReader reader, SnapEntryType type)
    {
        EnsureEntryExists(reader, type);
        return reader.ReadPrimitiveArray<byte>(type);
    }

    private static string[] ReadRequiredStrings(SnapReader reader, SnapEntryType type)
    {
        EnsureEntryExists(reader, type);
        return reader.ReadUtf8StringArray(type);
    }

    private static int[][] ReadRequiredDynamicInts(SnapReader reader, SnapEntryType type)
    {
        EnsureEntryExists(reader, type);
        return reader.ReadDynamicPrimitiveArrays<int>(type);
    }

    private static byte[][] ReadRequiredDynamicBytes(SnapReader reader, SnapEntryType type)
    {
        EnsureEntryExists(reader, type);
        return reader.ReadDynamicByteArrays(type);
    }

    private static DecodedVirtualMachineInfo ReadVirtualMachineInfo(SnapReader reader)
    {
        EnsureEntryExists(reader, SnapEntryType.Metadata_VirtualMachineInformation);
        var values = reader.ReadPrimitiveArray<uint>(SnapEntryType.Metadata_VirtualMachineInformation);
        if (values.Length < 6)
        {
            throw new InvalidOperationException(
                $"Metadata_VirtualMachineInformation expected at least 6 uints, found {values.Length}.");
        }

        return new DecodedVirtualMachineInfo
        {
            PointerSize = values[0],
            ObjectHeaderSize = values[1],
            ArrayHeaderSize = values[2],
            ArrayBoundsOffsetInHeader = values[3],
            ArraySizeOffsetInHeader = values[4],
            AllocationGranularity = values[5],
        };
    }

    private static ulong[] ReadManagedHeapSectionStartAddresses(SnapReader reader, uint formatVersion)
    {
        var starts = ReadRequiredULongs(reader, SnapEntryType.ManagedHeapSections_StartAddress);
        if (formatVersion < SnapFormatVersion.MemLabelSizeAndHeapIdVersion)
            return starts;

        var unmasked = new ulong[starts.Length];
        for (var i = 0; i < starts.Length; i++)
            unmasked[i] = starts[i] & ~HeapSectionTypeFlagMask;
        return unmasked;
    }

    /// <summary>
    /// Decodes each managed heap section's type from the high bit of its start address.
    /// Set bit means a virtual machine section; cleared bit (or pre-v12 formats with no flag) means a GC section.
    /// Mirrors Unity Memory Profiler's <c>ManagedMemorySectionEntriesCache</c>.
    /// </summary>
    private static ManagedHeapSectionKind[] ReadManagedHeapSectionTypes(SnapReader reader, uint formatVersion)
    {
        var starts = ReadRequiredULongs(reader, SnapEntryType.ManagedHeapSections_StartAddress);
        var types = new ManagedHeapSectionKind[starts.Length];
        if (formatVersion < SnapFormatVersion.MemLabelSizeAndHeapIdVersion)
            return types;

        for (var i = 0; i < starts.Length; i++)
        {
            types[i] = (starts[i] & HeapSectionTypeFlagMask) == HeapSectionTypeFlagMask
                ? ManagedHeapSectionKind.VirtualMachine
                : ManagedHeapSectionKind.GarbageCollector;
        }

        return types;
    }

    /// <summary>
    /// Reads the <c>ProfileTarget_MemoryStats</c> blob (a single fixed-size struct) and extracts the
    /// fields the summary builder needs. Returns null when the entry is absent or too small.
    /// </summary>
    private static DecodedTargetMemoryStats? ReadTargetMemoryStats(SnapReader reader)
    {
        if (!reader.HasEntry(SnapEntryType.ProfileTarget_MemoryStats))
            return null;

        byte[] bytes;
        try
        {
            // The entry's stored element size/count does not describe the full struct, so read the leading
            // struct bytes directly from the entry's block (mirrors Unity's ReadUnsafe(..., sizeof(struct), 0, 1)).
            bytes = reader.ReadEntryLeadingBytes(SnapEntryType.ProfileTarget_MemoryStats, 40);
        }
        catch
        {
            return null;
        }

        // Sequential ulong fields: TotalVirtualMemory@0, TotalUsedMemory@8, TotalReservedMemory@16,
        // TempAllocatorUsedMemory@24, GraphicsUsedMemory@32.
        if (bytes.Length < 40)
            return null;

        return new DecodedTargetMemoryStats
        {
            TotalVirtualMemory = BitConverter.ToUInt64(bytes, 0),
            GraphicsUsedMemory = BitConverter.ToUInt64(bytes, 32),
        };
    }

    private static ulong[] ReadInstanceIds(SnapReader reader, uint formatVersion)
    {
        if (!reader.HasEntry(SnapEntryType.NativeObjects_InstanceId))
            return [];

        if (formatVersion >= SnapFormatVersion.EntityIDAs8ByteStructs)
            return reader.ReadPrimitiveArray<ulong>(SnapEntryType.NativeObjects_InstanceId);

        var ids32 = reader.ReadPrimitiveArray<int>(SnapEntryType.NativeObjects_InstanceId);
        var ids = new ulong[ids32.Length];
        for (var i = 0; i < ids32.Length; i++)
            ids[i] = unchecked((uint)ids32[i]);
        return ids;
    }

    private static int[] ReadNativeObjectGcHandleIndices(SnapReader reader, uint formatVersion, int nativeObjectCount)
    {
        if (formatVersion < SnapFormatVersion.NativeConnectionsAsInstanceIdsVersion)
            return Enumerable.Repeat(-1, nativeObjectCount).ToArray();

        var gcHandleIndices = ReadOptionalInts(reader, SnapEntryType.NativeObjects_GCHandleIndex);
        if (gcHandleIndices.Length == 0)
            gcHandleIndices = ReadOptionalInts(reader, SnapEntryType.NativeObjects_GCHandleIndex_Legacy);
        if (gcHandleIndices.Length == nativeObjectCount)
            return gcHandleIndices;

        var fallback = Enumerable.Repeat(-1, nativeObjectCount).ToArray();
        if (gcHandleIndices.Length == 0)
            return fallback;

        Array.Copy(gcHandleIndices, fallback, Math.Min(gcHandleIndices.Length, fallback.Length));
        return fallback;
    }

    private static (int[] from, int[] to) ReadConnections(
        SnapReader reader,
        uint formatVersion,
        ulong[] nativeObjectInstanceIds,
        int[] nativeObjectGcHandleIndices,
        int gcHandleCount)
    {
        if (!reader.HasEntry(SnapEntryType.Connections_From) || !reader.HasEntry(SnapEntryType.Connections_To))
            return ([], []);

        if (formatVersion < SnapFormatVersion.NativeConnectionsAsInstanceIdsVersion)
        {
            var fromUnified = reader.ReadPrimitiveArray<int>(SnapEntryType.Connections_From);
            var toUnified = reader.ReadPrimitiveArray<int>(SnapEntryType.Connections_To);
            if (fromUnified.Length != toUnified.Length)
                throw new InvalidOperationException($"Array length mismatch for Connections_To. expected={fromUnified.Length}, actual={toUnified.Length}");
            return (fromUnified, toUnified);
        }

        ulong[] fromInstanceIds;
        ulong[] toInstanceIds;
        if (formatVersion >= SnapFormatVersion.EntityIDAs8ByteStructs)
        {
            fromInstanceIds = reader.ReadPrimitiveArray<ulong>(SnapEntryType.Connections_From);
            toInstanceIds = reader.ReadPrimitiveArray<ulong>(SnapEntryType.Connections_To);
        }
        else
        {
            var from32 = reader.ReadPrimitiveArray<int>(SnapEntryType.Connections_From);
            var to32 = reader.ReadPrimitiveArray<int>(SnapEntryType.Connections_To);
            fromInstanceIds = new ulong[from32.Length];
            toInstanceIds = new ulong[to32.Length];
            for (var i = 0; i < from32.Length; i++)
                fromInstanceIds[i] = unchecked((uint)from32[i]);
            for (var i = 0; i < to32.Length; i++)
                toInstanceIds[i] = unchecked((uint)to32[i]);
        }

        if (fromInstanceIds.Length != toInstanceIds.Length)
            throw new InvalidOperationException($"Array length mismatch for Connections_To. expected={fromInstanceIds.Length}, actual={toInstanceIds.Length}");

        var instanceIdToUnifiedIndex = new Dictionary<ulong, int>(nativeObjectInstanceIds.Length);
        var instanceIdToGcHandleIndex = new Dictionary<ulong, int>(nativeObjectInstanceIds.Length);
        for (var i = 0; i < nativeObjectInstanceIds.Length; i++)
        {
            var instanceId = nativeObjectInstanceIds[i];
            instanceIdToUnifiedIndex[instanceId] = gcHandleCount + i;
            var gcHandleIndex = i < nativeObjectGcHandleIndices.Length ? nativeObjectGcHandleIndices[i] : -1;
            if (gcHandleIndex >= 0)
                instanceIdToGcHandleIndex[instanceId] = gcHandleIndex;
        }

        var remappedFrom = new List<int>(fromInstanceIds.Length + instanceIdToGcHandleIndex.Count);
        var remappedTo = new List<int>(toInstanceIds.Length + instanceIdToGcHandleIndex.Count);
        for (var i = 0; i < fromInstanceIds.Length; i++)
        {
            if (!instanceIdToUnifiedIndex.TryGetValue(fromInstanceIds[i], out var fromUnified))
                continue;
            if (!instanceIdToUnifiedIndex.TryGetValue(toInstanceIds[i], out var toUnified))
                continue;
            remappedFrom.Add(fromUnified);
            remappedTo.Add(toUnified);
        }

        foreach (var (instanceId, gcHandleIndex) in instanceIdToGcHandleIndex)
        {
            if (!instanceIdToUnifiedIndex.TryGetValue(instanceId, out var fromUnified))
                continue;
            remappedFrom.Add(fromUnified);
            remappedTo.Add(gcHandleIndex);
        }

        return (remappedFrom.ToArray(), remappedTo.ToArray());
    }

    private static int[] ReadOptionalInts(SnapReader reader, SnapEntryType type)
    {
        if (!reader.HasEntry(type))
            return [];

        try
        {
            return reader.ReadPrimitiveArray<int>(type);
        }
        catch
        {
            return [];
        }
    }

    private static long[] ReadLongs(SnapReader reader, SnapEntryType type)
        => reader.HasEntry(type) ? reader.ReadPrimitiveArray<long>(type) : [];

    private static long[] ReadLongsWithCount(SnapReader reader, SnapEntryType type, int fallbackCount, long fallbackValue = 0)
    {
        var values = ReadOptionalLongs(reader, type);
        if (values.Length > 0)
            return values;

        return fallbackCount > 0 ? Enumerable.Repeat(fallbackValue, fallbackCount).ToArray() : [];
    }

    private static long[] ReadOptionalLongs(SnapReader reader, SnapEntryType type)
    {
        if (!reader.HasEntry(type))
            return [];

        try
        {
            return reader.ReadPrimitiveArray<long>(type);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Reads a page-size entry (resident 92 or swapped 97) as a raw byte count: a uint[] or ulong[]
    /// whose element [0] holds the page size. Returns 0 when the entry is absent or unreadable.
    /// </summary>
    private static ulong ReadPageSize(SnapReader reader, SnapEntryType type)
    {
        if (!reader.HasEntry(type))
            return 0;

        try
        {
            var uints = reader.ReadPrimitiveArray<uint>(type);
            if (uints.Length > 0)
                return uints[0];
        }
        catch
        {
            // Fall through to ulong read.
        }

        try
        {
            var ulongs = reader.ReadPrimitiveArray<ulong>(type);
            if (ulongs.Length > 0)
                return ulongs[0];
        }
        catch
        {
            // Entry missing or unsupported format.
        }

        return 0;
    }

    /// <summary>
    /// Reads a global page bitset entry (resident 91 or swapped 96): a single dynamic-size byte blob
    /// where bit i (LSB-first) is page i's state. Only element [0] is meaningful.
    /// </summary>
    private static byte[][] ReadPageStates(SnapReader reader, SnapEntryType type)
    {
        if (!reader.HasEntry(type))
            return [];

        try
        {
            var dynamic = reader.ReadDynamicByteArrays(type);
            if (dynamic.Length > 0 && dynamic[0].Length > 0)
                return new[] { dynamic[0] };
        }
        catch
        {
            // Fall through to constant-size blob read.
        }

        try
        {
            var blob = reader.ReadConstantRangeBytes(type, 0, 1);
            if (blob.Length > 0)
                return new[] { blob };
        }
        catch
        {
            // Entry missing or unsupported format.
        }

        return [];
    }

    private static byte[][] ReadOptionalDynamicBytes(SnapReader reader, SnapEntryType type)
    {
        if (!reader.HasEntry(type))
            return [];

        try
        {
            return reader.ReadDynamicByteArrays(type);
        }
        catch
        {
            return [];
        }
    }

    private static ulong[] ReadULongs(SnapReader reader, SnapEntryType type)
        => reader.HasEntry(type) ? reader.ReadPrimitiveArray<ulong>(type) : [];

    private static ulong[] ReadOptionalULongs(SnapReader reader, SnapEntryType type)
    {
        if (!reader.HasEntry(type))
            return [];
        try
        {
            return reader.ReadPrimitiveArray<ulong>(type);
        }
        catch
        {
            return [];
        }
    }

    private static void EnsureEntryExists(SnapReader reader, SnapEntryType type)
    {
        if (!reader.HasEntry(type))
            throw new InvalidOperationException($"Required snapshot entry '{type}' is missing.");
    }
}


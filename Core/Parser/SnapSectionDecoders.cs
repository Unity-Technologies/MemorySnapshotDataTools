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
            NativeObjectTypeIndices = nativeObjectTypeIndices,
            NativeObjectInstanceIds = nativeObjectInstanceIds,
            NativeObjectSizes = ReadULongs(reader, SnapEntryType.NativeObjects_Size),
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
            VirtualMachineInformation = ReadVirtualMachineInfo(reader),
            ManagedHeapSectionStartAddresses = ReadManagedHeapSectionStartAddresses(reader, formatVersion),
            ManagedHeapSectionBytes = ReadRequiredDynamicBytes(reader, SnapEntryType.ManagedHeapSections_Bytes),
            ManagedTypeFlags = ReadRequiredInts(reader, SnapEntryType.TypeDescriptions_Flags),
            ManagedTypeNames = ReadRequiredStrings(reader, SnapEntryType.TypeDescriptions_Name),
            ManagedTypeAssemblies = ReadRequiredStrings(reader, SnapEntryType.TypeDescriptions_Assembly),
            ManagedTypeFieldIndices = ReadRequiredDynamicInts(reader, SnapEntryType.TypeDescriptions_FieldIndices),
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

        EnsureArrayLength(snapshot.ManagedHeapSectionStartAddresses.Length, snapshot.ManagedHeapSectionBytes.Length, "ManagedHeapSections_Bytes");

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


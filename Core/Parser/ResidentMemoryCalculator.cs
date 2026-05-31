using System.Collections;

namespace MemorySnapshotDataTools.Parser;

/// <summary>
/// Computes per-native-object and per-allocation resident memory sizes by intersecting
/// address ranges with OS page residency bitmaps from <c>SystemMemoryResidentPages_*</c> entries.
/// Only produces non-zero values for format v17+ snapshots with resident page data.
/// </summary>
internal static class ResidentMemoryCalculator
{
    /// <summary>
    /// Returns whether per-object resident sizes can be computed from page bitmap data.
    /// </summary>
    public static bool HasPerObjectResident(DecodedSnapshot decoded) =>
        decoded.FormatVersion >= SnapFormatVersion.SystemMemoryResidentPagesVersion &&
        decoded.SystemMemoryResidentPageAddresses.Length > 0 &&
        decoded.SystemMemoryResidentPageSize > 0 &&
        decoded.SystemMemoryResidentPageStates.Length > 0 &&
        decoded.SystemMemoryResidentPageStates[0].Length > 0;

    /// <summary>
    /// Computes <c>resident_size_bytes</c> for each native object address range.
    /// </summary>
    public static ulong[] ComputePerObject(DecodedSnapshot decoded)
    {
        var count = decoded.NativeObjectNames.Length;
        var result = new ulong[count];
        if (!HasPerObjectResident(decoded))
            return result;

        var pageStates = new BitArray(decoded.SystemMemoryResidentPageStates[0]);
        var pageSize = decoded.SystemMemoryResidentPageSize;

        for (var i = 0; i < count; i++)
        {
            if (i >= decoded.NativeObjectAddresses.Length || i >= decoded.NativeObjectSizes.Length)
                continue;

            var address = decoded.NativeObjectAddresses[i];
            var size = decoded.NativeObjectSizes[i];
            if (size == 0)
                continue;

            var regionIndex = FindResidentPageRegionIndex(decoded, address);
            if (regionIndex < 0)
                continue;

            result[i] = CalculateResidentForRange(
                decoded,
                pageStates,
                pageSize,
                regionIndex,
                address,
                size);
        }

        return result;
    }

    /// <summary>
    /// Computes resident bytes for each native allocation.
    /// </summary>
    public static ulong[] ComputePerAllocation(DecodedSnapshot decoded)
    {
        var count = decoded.NativeAllocationAddresses.Length;
        var result = new ulong[count];
        if (!HasPerObjectResident(decoded))
            return result;

        var pageStates = new BitArray(decoded.SystemMemoryResidentPageStates[0]);
        var pageSize = decoded.SystemMemoryResidentPageSize;

        for (var i = 0; i < count; i++)
        {
            var address = decoded.NativeAllocationAddresses[i];
            var size = decoded.NativeAllocationSizes[i];
            if (size == 0)
                continue;

            var regionIndex = FindResidentPageRegionIndex(decoded, address);
            if (regionIndex < 0)
                continue;

            result[i] = CalculateResidentForRange(
                decoded,
                pageStates,
                pageSize,
                regionIndex,
                address,
                size);
        }

        return result;
    }

    /// <summary>
    /// Aggregates allocation resident bytes onto native roots by <c>RootReferenceId</c>,
    /// and adds native object resident bytes for objects linked to the same root.
    /// </summary>
    public static ulong[] ComputePerRoot(
        DecodedSnapshot decoded,
        ulong[] objectResidentSizes,
        ulong[] allocationResidentSizes)
    {
        var rootCount = decoded.NativeRootIds.Length;
        var result = new ulong[rootCount];
        if (!HasPerObjectResident(decoded))
            return result;

        var rootIdToIndex = new Dictionary<long, int>(rootCount);
        for (var i = 0; i < rootCount; i++)
            rootIdToIndex[decoded.NativeRootIds[i]] = i;

        for (var i = 0; i < allocationResidentSizes.Length; i++)
        {
            if (i >= decoded.NativeAllocationRootReferenceIds.Length)
                continue;

            var rootId = decoded.NativeAllocationRootReferenceIds[i];
            if (rootId < 0 || !rootIdToIndex.TryGetValue(rootId, out var rootIndex))
                continue;

            result[rootIndex] += allocationResidentSizes[i];
        }

        for (var i = 0; i < objectResidentSizes.Length; i++)
        {
            if (i >= decoded.NativeObjectRootReferenceIds.Length)
                continue;

            var rootId = decoded.NativeObjectRootReferenceIds[i];
            if (rootId < 0 || !rootIdToIndex.TryGetValue(rootId, out var rootIndex))
                continue;

            result[rootIndex] += objectResidentSizes[i];
        }

        return result;
    }

    private static int FindResidentPageRegionIndex(DecodedSnapshot decoded, ulong address)
    {
        var regions = decoded.SystemMemoryResidentPageAddresses;
        var best = -1;
        for (var i = 0; i < regions.Length; i++)
        {
            if (address < regions[i])
                break;

            var regionEnd = i + 1 < regions.Length
                ? regions[i + 1]
                : ulong.MaxValue;

            if (address < regionEnd || i == regions.Length - 1)
            {
                best = i;
                break;
            }

            best = i;
        }

        return best;
    }

    internal static ulong CalculateResidentForRange(
        DecodedSnapshot decoded,
        BitArray pageStates,
        ulong pageSizeUlong,
        int regionIndex,
        ulong address,
        ulong size)
    {
        if (size == 0 || pageSizeUlong == 0)
            return 0;

        var pageSize = pageSizeUlong;
        var regionAddress = decoded.SystemMemoryResidentPageAddresses[regionIndex];
        var firstPageIndex = decoded.SystemMemoryResidentPageFirstIndices[regionIndex];
        var lastPageIndex = decoded.SystemMemoryResidentPageLastIndices[regionIndex];

        var addrDelta = address - regionAddress;
        var begPage = (int)(addrDelta / pageSize) + firstPageIndex;
        var endPage = (int)((addrDelta + size - 1) / pageSize) + firstPageIndex;

        if (begPage < firstPageIndex || endPage > lastPageIndex)
            return 0;

        ulong residentSize = 0;
        for (var p = begPage; p <= endPage; p++)
        {
            if (p >= 0 && p < pageStates.Length && pageStates[p])
                residentSize += pageSize;
        }

        if (begPage >= 0 && begPage < pageStates.Length && pageStates[begPage])
        {
            var head = address % pageSize;
            residentSize -= head;
        }

        if (endPage >= 0 && endPage < pageStates.Length && pageStates[endPage])
        {
            var tail = (address + size) % pageSize;
            if (tail > 0)
                residentSize -= pageSize - tail;
        }

        return residentSize;
    }
}

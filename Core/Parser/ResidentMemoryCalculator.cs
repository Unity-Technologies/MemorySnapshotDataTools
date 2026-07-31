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

    private static int FindResidentPageRegionIndex(DecodedSnapshot decoded, ulong address) =>
        FindPageRegionIndex(decoded.SystemMemoryResidentPageAddresses, address);

    /// <summary>
    /// Finds the page-bitmap range containing <paramref name="address"/> in a sorted range-address
    /// array (resident or swapped), or -1 when the address precedes every range.
    /// </summary>
    internal static int FindPageRegionIndex(ulong[] rangeAddresses, ulong address)
    {
        var best = -1;
        for (var i = 0; i < rangeAddresses.Length; i++)
        {
            if (address < rangeAddresses[i])
                break;

            var regionEnd = i + 1 < rangeAddresses.Length
                ? rangeAddresses[i + 1]
                : ulong.MaxValue;

            if (address < regionEnd || i == rangeAddresses.Length - 1)
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
        => CalculateBytesForRange(
            decoded.SystemMemoryResidentPageAddresses,
            decoded.SystemMemoryResidentPageFirstIndices,
            decoded.SystemMemoryResidentPageLastIndices,
            pageStates,
            pageSizeUlong,
            regionIndex,
            address,
            size);

    /// <summary>
    /// Shared page-bitmap kernel: intersects an address range with a global page bitset (resident or
    /// swapped) and returns the byte total, trimming the partial head/tail of set boundary pages.
    /// </summary>
    /// <param name="rangeAddresses">Per-range base addresses (same geometry for resident and swapped).</param>
    /// <param name="firstPageIndices">Per-range first page index in the global bitmap.</param>
    /// <param name="lastPageIndices">Per-range last page index in the global bitmap.</param>
    /// <param name="pageStates">Global page bitset (bit i = page i set, LSB-first).</param>
    /// <param name="pageSizeUlong">Page size in bytes.</param>
    /// <param name="regionIndex">Range index into the geometry arrays.</param>
    /// <param name="address">Start address of the queried range.</param>
    /// <param name="size">Size in bytes of the queried range.</param>
    internal static ulong CalculateBytesForRange(
        ulong[] rangeAddresses,
        int[] firstPageIndices,
        int[] lastPageIndices,
        BitArray pageStates,
        ulong pageSizeUlong,
        int regionIndex,
        ulong address,
        ulong size)
    {
        if (size == 0 || pageSizeUlong == 0)
            return 0;

        if (regionIndex < 0 ||
            regionIndex >= rangeAddresses.Length ||
            regionIndex >= firstPageIndices.Length ||
            regionIndex >= lastPageIndices.Length)
        {
            return 0;
        }

        var pageSize = pageSizeUlong;
        var regionAddress = rangeAddresses[regionIndex];
        var firstPageIndex = firstPageIndices[regionIndex];
        var lastPageIndex = lastPageIndices[regionIndex];

        var addrDelta = address - regionAddress;
        var begPage = (int)(addrDelta / pageSize) + firstPageIndex;
        var endPage = (int)((addrDelta + size - 1) / pageSize) + firstPageIndex;

        if (begPage < firstPageIndex || endPage > lastPageIndex)
            return 0;

        ulong totalSize = 0;
        for (var p = begPage; p <= endPage; p++)
        {
            if (p >= 0 && p < pageStates.Length && pageStates[p])
                totalSize += pageSize;
        }

        if (begPage >= 0 && begPage < pageStates.Length && pageStates[begPage])
        {
            var head = address % pageSize;
            totalSize -= head;
        }

        if (endPage >= 0 && endPage < pageStates.Length && pageStates[endPage])
        {
            var tail = (address + size) % pageSize;
            if (tail > 0)
                totalSize -= pageSize - tail;
        }

        return totalSize;
    }
}

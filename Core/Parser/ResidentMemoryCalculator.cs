using System.Collections;

namespace MemorySnapshotDataTools.Parser;

/// <summary>
/// Computes per-native-object, per-allocation, and per-memory-region resident memory sizes by
/// intersecting address ranges with OS page residency bitmaps from <c>SystemMemoryResidentPages_*</c>
/// entries. Resident bytes = whole OS pages backed by physical RAM (counts against process RSS), as
/// opposed to reserved address space or live/allocated bytes.
/// Only produces non-null/non-zero values for format v17+ snapshots with resident page data.
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
    /// Computes per-native-memory-region resident bytes: the number of bytes inside each Unity
    /// native memory region that are actually backed by physical RAM at snapshot time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three related-but-distinct concepts apply to a memory region:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Reserved</b> — the size of the address-space range the allocator asked the OS for
    /// (<c>NativeMemoryRegionAddressSizes</c> / the <c>address_size</c> column). Reserving address space
    /// costs no physical memory on its own.
    /// </description></item>
    /// <item><description>
    /// <b>Allocated (live)</b> — the bytes actively handed out to callers inside the region
    /// (the sum of <c>native_allocations.size_bytes</c> whose <c>memory_region_index</c> is this region).
    /// </description></item>
    /// <item><description>
    /// <b>Resident</b> — the value computed here: whole OS pages within the reserved range that are
    /// currently paged into physical RAM. This is what counts against the process RSS. It is measured at
    /// page granularity (typically 4 KiB or 16 KiB), so it is neither the reserved size nor the live size:
    /// a page counts as resident if any part of it is backed, and partial head/tail pages that fall
    /// outside the region range are trimmed off.
    /// </description></item>
    /// </list>
    /// <para>
    /// Returns an array indexed by native memory region. An entry is <c>null</c> when it cannot be
    /// determined: the whole array is <c>null</c>-filled when the snapshot carries no residency bitmap
    /// (format &lt; 17, so residency is <i>unknown</i> rather than zero), and an individual entry is
    /// <c>null</c> when the region reserves no address space (<c>size == 0</c>). An entry is <c>0</c> when
    /// the region is genuinely covered by residency data but no backing page overlaps it.
    /// </para>
    /// </remarks>
    /// <param name="decoded">The decoded snapshot.</param>
    /// <returns>Per-region resident bytes; <c>null</c> entries mean "unknown", <c>0</c> means "none resident".</returns>
    public static ulong?[] ComputePerRegion(DecodedSnapshot decoded)
    {
        var count = decoded.NativeMemoryRegionAddressBases.Length;
        var result = new ulong?[count];

        // No residency bitmap in this snapshot (format < 17): residency is UNKNOWN, not zero.
        // Leave every entry null so the exported column is NULL rather than a misleading 0.
        if (!HasPerObjectResident(decoded))
            return result;

        var pageStates = new BitArray(decoded.SystemMemoryResidentPageStates[0]);
        var pageSize = decoded.SystemMemoryResidentPageSize;

        for (var i = 0; i < count; i++)
        {
            var baseAddress = decoded.NativeMemoryRegionAddressBases[i];
            var size = i < decoded.NativeMemoryRegionAddressSizes.Length
                ? decoded.NativeMemoryRegionAddressSizes[i]
                : 0UL;

            // A region that reserves no address space has no meaningful resident value → null.
            if (size == 0)
            {
                result[i] = null;
                continue;
            }

            // Locate the resident-page range that covers this region's base address. If none covers it,
            // the region's pages are simply absent from the bitmap → 0 bytes resident (known, not unknown).
            var regionIndex = FindResidentPageRegionIndex(decoded, baseAddress);
            if (regionIndex < 0)
            {
                result[i] = 0;
                continue;
            }

            // CalculateResidentForRange maps [base, base+size) onto pages in the covering range, sums
            // pageSize per set residency bit, and trims the partial head/tail pages. Its existing guard
            // returns 0 when the range would extend past the covering page range (i.e. the region would
            // span more than one resident-page range). That edge case was NOT observed in the test data;
            // treating it as 0 keeps behavior identical to the per-object/per-allocation calculators and
            // never over-counts. If it ever needs exact handling, the range would have to be split across
            // consecutive resident-page ranges here.
            result[i] = CalculateResidentForRange(
                decoded,
                pageStates,
                pageSize,
                regionIndex,
                baseAddress,
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

using System.Collections;

namespace MemorySnapshotDataTools.Parser;

/// <summary>
/// Computes per-native-object and per-allocation swapped memory sizes by intersecting
/// address ranges with OS page swap bitmaps from the optional <c>SystemMemorySwappedPages_*</c>
/// entries (93–97, appended at format v17). A page is either resident or swapped, never both;
/// swapped bytes are a subset of (committed − resident). Only produces non-zero values when the
/// swapped-page entries are present — gate on entry presence, never on format version.
/// Mirrors <see cref="ResidentMemoryCalculator"/> and shares its page-bitmap kernel.
/// </summary>
internal static class SwappedMemoryCalculator
{
    /// <summary>
    /// Returns whether per-object swapped sizes can be computed from swapped page bitmap data.
    /// </summary>
    public static bool HasPerObjectSwapped(DecodedSnapshot decoded) =>
        decoded.FormatVersion >= SnapFormatVersion.SystemMemoryResidentPagesVersion &&
        decoded.SystemMemorySwappedPageAddresses.Length > 0 &&
        decoded.SystemMemorySwappedPageSize > 0 &&
        decoded.SystemMemorySwappedPageStates.Length > 0 &&
        decoded.SystemMemorySwappedPageStates[0].Length > 0;

    /// <summary>
    /// Computes swapped bytes for each native object address range.
    /// </summary>
    public static ulong[] ComputePerObject(DecodedSnapshot decoded)
    {
        var count = decoded.NativeObjectNames.Length;
        var result = new ulong[count];
        if (!HasPerObjectSwapped(decoded))
            return result;

        var pageStates = new BitArray(decoded.SystemMemorySwappedPageStates[0]);
        var pageSize = decoded.SystemMemorySwappedPageSize;

        for (var i = 0; i < count; i++)
        {
            if (i >= decoded.NativeObjectAddresses.Length || i >= decoded.NativeObjectSizes.Length)
                continue;

            var address = decoded.NativeObjectAddresses[i];
            var size = decoded.NativeObjectSizes[i];
            if (size == 0)
                continue;

            var regionIndex = ResidentMemoryCalculator.FindPageRegionIndex(decoded.SystemMemorySwappedPageAddresses, address);
            if (regionIndex < 0)
                continue;

            result[i] = CalculateSwappedForRange(
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
    /// Computes swapped bytes for each native allocation.
    /// </summary>
    public static ulong[] ComputePerAllocation(DecodedSnapshot decoded)
    {
        var count = decoded.NativeAllocationAddresses.Length;
        var result = new ulong[count];
        if (!HasPerObjectSwapped(decoded))
            return result;

        var pageStates = new BitArray(decoded.SystemMemorySwappedPageStates[0]);
        var pageSize = decoded.SystemMemorySwappedPageSize;

        for (var i = 0; i < count; i++)
        {
            var address = decoded.NativeAllocationAddresses[i];
            var size = decoded.NativeAllocationSizes[i];
            if (size == 0)
                continue;

            var regionIndex = ResidentMemoryCalculator.FindPageRegionIndex(decoded.SystemMemorySwappedPageAddresses, address);
            if (regionIndex < 0)
                continue;

            result[i] = CalculateSwappedForRange(
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
    /// Aggregates allocation swapped bytes onto native roots by <c>RootReferenceId</c>,
    /// and adds native object swapped bytes for objects linked to the same root.
    /// </summary>
    public static ulong[] ComputePerRoot(
        DecodedSnapshot decoded,
        ulong[] objectSwappedSizes,
        ulong[] allocationSwappedSizes)
    {
        var rootCount = decoded.NativeRootIds.Length;
        var result = new ulong[rootCount];
        if (!HasPerObjectSwapped(decoded))
            return result;

        var rootIdToIndex = new Dictionary<long, int>(rootCount);
        for (var i = 0; i < rootCount; i++)
            rootIdToIndex[decoded.NativeRootIds[i]] = i;

        for (var i = 0; i < allocationSwappedSizes.Length; i++)
        {
            if (i >= decoded.NativeAllocationRootReferenceIds.Length)
                continue;

            var rootId = decoded.NativeAllocationRootReferenceIds[i];
            if (rootId < 0 || !rootIdToIndex.TryGetValue(rootId, out var rootIndex))
                continue;

            result[rootIndex] += allocationSwappedSizes[i];
        }

        for (var i = 0; i < objectSwappedSizes.Length; i++)
        {
            if (i >= decoded.NativeObjectRootReferenceIds.Length)
                continue;

            var rootId = decoded.NativeObjectRootReferenceIds[i];
            if (rootId < 0 || !rootIdToIndex.TryGetValue(rootId, out var rootIndex))
                continue;

            result[rootIndex] += objectSwappedSizes[i];
        }

        return result;
    }

    /// <summary>
    /// Computes swapped bytes for each OS system memory region (the swapped analogue of the
    /// per-region <c>SystemMemoryRegions_Resident</c> entry, which has no swapped counterpart
    /// in the snapshot and must be derived from the bitmap).
    /// </summary>
    public static ulong[] ComputePerSystemRegion(DecodedSnapshot decoded)
    {
        var count = decoded.SystemMemoryRegionAddresses.Length;
        var result = new ulong[count];
        if (!HasPerObjectSwapped(decoded))
            return result;

        var pageStates = new BitArray(decoded.SystemMemorySwappedPageStates[0]);
        var pageSize = decoded.SystemMemorySwappedPageSize;

        for (var i = 0; i < count; i++)
        {
            var size = i < decoded.SystemMemoryRegionSizes.Length ? decoded.SystemMemoryRegionSizes[i] : 0;
            if (size == 0)
                continue;

            // Swapped-page ranges share the system-region geometry (one range per region).
            result[i] = CalculateSwappedForRange(
                decoded,
                pageStates,
                pageSize,
                i,
                decoded.SystemMemoryRegionAddresses[i],
                size);
        }

        return result;
    }

    internal static ulong CalculateSwappedForRange(
        DecodedSnapshot decoded,
        BitArray pageStates,
        ulong pageSizeUlong,
        int regionIndex,
        ulong address,
        ulong size)
        => ResidentMemoryCalculator.CalculateBytesForRange(
            decoded.SystemMemorySwappedPageAddresses,
            decoded.SystemMemorySwappedPageFirstIndices,
            decoded.SystemMemorySwappedPageLastIndices,
            pageStates,
            pageSizeUlong,
            regionIndex,
            address,
            size);
}

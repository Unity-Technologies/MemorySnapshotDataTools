using System.Collections;

namespace MemorySnapshotDataTools.Parser;

/// <summary>
/// Assigns resident bytes to native roots and objects by walking sorted address points,
/// mirroring <c>EntriesMemoryMapCache.ForEachFlatWithResidentSize</c> in the Unity Memory Profiler.
/// </summary>
internal static class MemoryMapResidentAggregator
{
    private enum PointKind
    {
        End = 0,
        Start = 1,
        SystemMemoryRegion = 2,
    }

    private readonly record struct MemoryPoint(ulong Address, PointKind Kind, int Index, bool IsAllocation);

    /// <summary>
    /// Per-root and per-object resident and swapped sizes computed by <see cref="Compute"/>.
    /// The swapped arrays are null when the optional swapped-page entries are absent.
    /// </summary>
    internal sealed class MemoryMapSizes
    {
        /// <summary>Resident bytes per native root.</summary>
        public required ulong[] RootResidentSizes { get; init; }

        /// <summary>Resident bytes per native object.</summary>
        public required ulong[] ObjectResidentSizes { get; init; }

        /// <summary>Swapped bytes per native root, or null when swapped-page entries are absent.</summary>
        public required ulong[]? RootSwappedSizes { get; init; }

        /// <summary>Swapped bytes per native object, or null when swapped-page entries are absent.</summary>
        public required ulong[]? ObjectSwappedSizes { get; init; }
    }

    /// <summary>
    /// Computes per-root and per-object resident (and, when the optional swapped-page entries are
    /// present, swapped) sizes from page bitmap data and address layout in a single walk.
    /// </summary>
    public static MemoryMapSizes Compute(DecodedSnapshot decoded)
    {
        var rootCount = decoded.NativeRootIds.Length;
        var objectCount = decoded.NativeObjectNames.Length;
        var rootResident = new ulong[rootCount];
        var objectResident = new ulong[objectCount];

        var hasResident = ResidentMemoryCalculator.HasPerObjectResident(decoded);
        var hasSwapped = SwappedMemoryCalculator.HasPerObjectSwapped(decoded);
        var rootSwapped = hasSwapped ? new ulong[rootCount] : null;
        var objectSwapped = hasSwapped ? new ulong[objectCount] : null;

        var result = new MemoryMapSizes
        {
            RootResidentSizes = rootResident,
            ObjectResidentSizes = objectResident,
            RootSwappedSizes = rootSwapped,
            ObjectSwappedSizes = objectSwapped,
        };

        if (!hasResident && !hasSwapped)
            return result;

        var rootIdToIndex = BuildRootIdToIndex(decoded);
        var points = BuildSortedPoints(decoded);
        if (points.Count < 2)
            return result;

        var residentStates = hasResident ? new BitArray(decoded.SystemMemoryResidentPageStates[0]) : null;
        var residentPageSize = decoded.SystemMemoryResidentPageSize;
        var swappedStates = hasSwapped ? new BitArray(decoded.SystemMemorySwappedPageStates[0]) : null;
        var swappedPageSize = decoded.SystemMemorySwappedPageSize;
        var currentRegionIndex = -1;

        for (var i = 0; i < points.Count - 1; i++)
        {
            var cur = points[i];
            var next = points[i + 1];

            if (cur.Kind == PointKind.SystemMemoryRegion)
                currentRegionIndex = cur.Index;

            if (cur.Kind != PointKind.Start || currentRegionIndex < 0)
                continue;

            var size = next.Address - cur.Address;
            if (size == 0)
                continue;

            var resident = residentStates != null
                ? ResidentMemoryCalculator.CalculateResidentForRange(
                    decoded,
                    residentStates,
                    residentPageSize,
                    currentRegionIndex,
                    cur.Address,
                    size)
                : 0;

            var swapped = swappedStates != null
                ? SwappedMemoryCalculator.CalculateSwappedForRange(
                    decoded,
                    swappedStates,
                    swappedPageSize,
                    currentRegionIndex,
                    cur.Address,
                    size)
                : 0;

            if (cur.IsAllocation)
            {
                if (cur.Index >= decoded.NativeAllocationRootReferenceIds.Length)
                    continue;

                var rootReferenceId = decoded.NativeAllocationRootReferenceIds[cur.Index];
                if (rootReferenceId < 1)
                    continue;

                if (rootIdToIndex.TryGetValue(rootReferenceId, out var allocationRootIndex))
                {
                    rootResident[allocationRootIndex] += resident;
                    if (rootSwapped != null)
                        rootSwapped[allocationRootIndex] += swapped;
                }
            }
            else if (cur.Kind == PointKind.Start)
            {
                objectResident[cur.Index] += resident;
                if (objectSwapped != null)
                    objectSwapped[cur.Index] += swapped;
                if (cur.Index < decoded.NativeObjectRootReferenceIds.Length)
                {
                    var rootReferenceId = decoded.NativeObjectRootReferenceIds[cur.Index];
                    if (rootReferenceId >= 1 &&
                        rootIdToIndex.TryGetValue(rootReferenceId, out var objectRootIndex))
                    {
                        rootResident[objectRootIndex] += resident;
                        if (rootSwapped != null)
                            rootSwapped[objectRootIndex] += swapped;
                    }
                }
            }
        }

        return result;
    }

    private static Dictionary<long, int> BuildRootIdToIndex(DecodedSnapshot decoded)
    {
        var map = new Dictionary<long, int>(decoded.NativeRootIds.Length);
        for (var i = 0; i < decoded.NativeRootIds.Length; i++)
            map[decoded.NativeRootIds[i]] = i;
        return map;
    }

    private static List<MemoryPoint> BuildSortedPoints(DecodedSnapshot decoded)
    {
        var points = new List<MemoryPoint>();

        for (var i = 0; i < decoded.SystemMemoryRegionAddresses.Length; i++)
            points.Add(new MemoryPoint(decoded.SystemMemoryRegionAddresses[i], PointKind.SystemMemoryRegion, i, false));

        for (var i = 0; i < decoded.NativeAllocationAddresses.Length; i++)
        {
            var address = decoded.NativeAllocationAddresses[i];
            if (address == 0)
                continue;

            var size = i < decoded.NativeAllocationSizes.Length ? decoded.NativeAllocationSizes[i] : 0;
            if (size == 0)
                continue;

            points.Add(new MemoryPoint(address, PointKind.Start, i, true));
            points.Add(new MemoryPoint(address + size, PointKind.End, i, true));
        }

        for (var i = 0; i < decoded.NativeObjectAddresses.Length; i++)
        {
            var address = decoded.NativeObjectAddresses[i];
            if (address == 0)
                continue;

            var size = i < decoded.NativeObjectSizes.Length ? decoded.NativeObjectSizes[i] : 0;
            if (size == 0)
                continue;

            points.Add(new MemoryPoint(address, PointKind.Start, i, false));
            points.Add(new MemoryPoint(address + size, PointKind.End, i, false));
        }

        points.Sort(static (a, b) =>
        {
            var cmp = a.Address.CompareTo(b.Address);
            if (cmp != 0)
                return cmp;

            return SortOrder(a).CompareTo(SortOrder(b));
        });

        return points;
    }

    private static int SortOrder(MemoryPoint point) => point.Kind switch
    {
        PointKind.End => 0,
        PointKind.SystemMemoryRegion => 1,
        PointKind.Start when point.IsAllocation => 2,
        PointKind.Start => 3,
        _ => 4,
    };
}

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
    /// Computes per-root and per-object resident sizes from page bitmap data and address layout.
    /// </summary>
    public static (ulong[] RootResidentSizes, ulong[] ObjectResidentSizes) Compute(DecodedSnapshot decoded)
    {
        var rootCount = decoded.NativeRootIds.Length;
        var objectCount = decoded.NativeObjectNames.Length;
        var rootResident = new ulong[rootCount];
        var objectResident = new ulong[objectCount];

        if (!ResidentMemoryCalculator.HasPerObjectResident(decoded))
            return (rootResident, objectResident);

        var rootIdToIndex = BuildRootIdToIndex(decoded);
        var points = BuildSortedPoints(decoded);
        if (points.Count < 2)
            return (rootResident, objectResident);

        var pageStates = new BitArray(decoded.SystemMemoryResidentPageStates[0]);
        var pageSize = decoded.SystemMemoryResidentPageSize;
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

            var resident = ResidentMemoryCalculator.CalculateResidentForRange(
                decoded,
                pageStates,
                pageSize,
                currentRegionIndex,
                cur.Address,
                size);

            if (cur.IsAllocation)
            {
                if (cur.Index >= decoded.NativeAllocationRootReferenceIds.Length)
                    continue;

                var rootReferenceId = decoded.NativeAllocationRootReferenceIds[cur.Index];
                if (rootReferenceId < 1)
                    continue;

                if (rootIdToIndex.TryGetValue(rootReferenceId, out var allocationRootIndex))
                    rootResident[allocationRootIndex] += resident;
            }
            else if (cur.Kind == PointKind.Start)
            {
                objectResident[cur.Index] += resident;
                if (cur.Index < decoded.NativeObjectRootReferenceIds.Length)
                {
                    var rootReferenceId = decoded.NativeObjectRootReferenceIds[cur.Index];
                    if (rootReferenceId >= 1 &&
                        rootIdToIndex.TryGetValue(rootReferenceId, out var objectRootIndex))
                    {
                        rootResident[objectRootIndex] += resident;
                    }
                }
            }
        }

        return (rootResident, objectResident);
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

using System.Collections;

namespace MemorySnapshotDataTools.Parser;

/// <summary>
/// Computes the Unity Memory Profiler "Summary" page metrics (Allocated Memory Distribution and
/// Managed Heap Utilization) from a decoded snapshot.
///
/// <para>This replicates <c>EntriesMemoryMapCache</c> (build/sort/post-process of an address spectrum),
/// <c>GetPointType</c> classification, and the post-processing in <c>AllMemorySummaryModelBuilder</c> /
/// <c>ManagedMemorySummaryModelBuilder</c> (legacy untracked fallback, graphics estimation, VM root
/// reassignment) so the tool's numbers can be validated against golden values exported from the Editor.</para>
/// </summary>
public static class SummaryMetricsCalculator
{
    // Category labels — must match Memory Profiler's SummaryTextContent (golden trims the trailing '*' from Untracked).
    private const string CategoryNative = "Native";
    private const string CategoryManaged = "Managed";
    private const string CategoryExecutablesAndMapped = "Executables & Mapped";
    private const string CategoryGraphics = "Graphics (Estimated)";
    private const string CategoryUntracked = "Untracked";
    private const string CategoryVirtualMachine = "Virtual Machine";
    private const string CategoryObjects = "Objects";
    private const string CategoryEmptyHeapSpace = "Empty Heap Space";

    // VM native root object names, matching Memory Profiler's NativeRootReferenceEntriesCache.k_VMRootNames.
    private static readonly string[] VmRootNames = ["Mono VM", "IL2CPP VM", "IL2CPPMemoryAllocator"];

    // Source kinds; byte values match Memory Profiler's CachedSnapshot.SourceIndex.SourceId ordering,
    // which the address-point sort tie-break depends on.
    private enum SourceKind : byte
    {
        None = 0,
        SystemMemoryRegion = 1,
        NativeMemoryRegion = 2,
        NativeAllocation = 3,
        ManagedHeapSection = 4,
        NativeObject = 5,
        ManagedObject = 6,
    }

    // OS system-memory region types, matching SystemMemoryRegionEntriesCache.MemoryType.
    private enum SystemRegionMemoryType
    {
        Private = 0,
        Mapped = 1,
        Shared = 2,
        Device = 3,
    }

    private enum PointType
    {
        Free,
        Untracked,
        NativeReserved,
        Native,
        ManagedReserved,
        Managed,
        Device,
        Mapped,
        Shared,
        AndroidRuntime,
    }

    private struct Mem
    {
        public ulong Committed;
        public ulong Resident;
    }

    private struct AddressPoint
    {
        public ulong Address;
        public long PairId;
        public SourceKind Kind;
        public int Index;
        public bool IsEnd;
    }

    /// <summary>
    /// Computes summary metrics from a decoded snapshot and its crawled managed objects.
    /// </summary>
    public static SummaryMetrics Compute(
        DecodedSnapshot decoded,
        IReadOnlyList<ManagedObjectRow> managedObjects)
    {
        var hasSystemRegions = decoded.SystemMemoryRegionAddresses.Length > 0;
        var hasResidentPages = ResidentMemoryCalculator.HasPerObjectResident(decoded);
        var isAndroid = (decoded.CaptureMetadata.Platform ?? string.Empty)
            .Contains("Android", StringComparison.OrdinalIgnoreCase);

        var points = BuildSortedPoints(decoded, managedObjects);
        PostProcess(points);

        var pageStates = hasResidentPages ? new BitArray(decoded.SystemMemoryResidentPageStates[0]) : null;
        var pageSize = decoded.SystemMemoryResidentPageSize;

        var native = default(Mem);
        var managed = default(Mem);
        var mapped = default(Mem);
        var graphics = default(Mem);
        var untracked = default(Mem);
        var androidRuntime = default(Mem);

        var vmSection = default(Mem);
        var emptyHeapSpace = default(Mem);
        var objects = default(Mem);

        // The VM root's size is the map-resolved committed/resident of the native allocations and objects
        // rooted to it (matching ProcessedNativeRoots), not the raw NativeRootReferences accumulated size.
        var vmRootIndex = FindVmRootIndex(decoded);
        var vmRootId = vmRootIndex >= 0 && vmRootIndex < decoded.NativeRootIds.Length
            ? decoded.NativeRootIds[vmRootIndex]
            : long.MinValue;
        var vmRoot = default(Mem);

        var currentSystemRegion = -1;
        for (var i = 0; i < points.Length - 1; i++)
        {
            var cur = points[i];
            if (cur.Kind == SourceKind.SystemMemoryRegion)
                currentSystemRegion = cur.Index;
            else if (cur.Kind == SourceKind.None)
                currentSystemRegion = -1;

            if (cur.Kind == SourceKind.None)
                continue;

            var size = points[i + 1].Address - cur.Address;
            if (size == 0)
                continue;

            var resident = 0UL;
            if (hasSystemRegions)
            {
                // Items outside any system region exist due to capture timing differences; skip them.
                if (currentSystemRegion < 0)
                    continue;

                if (pageStates != null && CanComputeResident(decoded, currentSystemRegion))
                {
                    resident = ResidentMemoryCalculator.CalculateResidentForRange(
                        decoded, pageStates, pageSize, currentSystemRegion, cur.Address, size);
                }
            }

            var span = new Mem { Committed = size, Resident = resident };
            if (vmRootId != long.MinValue && SpanRootReferenceId(decoded, cur) == vmRootId)
                Add(ref vmRoot, span);

            switch (GetPointType(decoded, cur, isAndroid))
            {
                case PointType.Native:
                case PointType.NativeReserved:
                    Add(ref native, span);
                    break;
                case PointType.Managed:
                case PointType.ManagedReserved:
                    Add(ref managed, span);
                    AddManagedBreakdown(decoded, cur, span, ref vmSection, ref emptyHeapSpace, ref objects);
                    break;
                case PointType.Mapped:
                    Add(ref mapped, span);
                    break;
                case PointType.Device:
                    Add(ref graphics, span);
                    break;
                case PointType.Shared:
                case PointType.Untracked:
                    Add(ref untracked, span);
                    break;
                case PointType.AndroidRuntime:
                    Add(ref androidRuntime, span);
                    break;
            }
        }

        var total = default(Mem);
        Add(ref total, native);
        Add(ref total, managed);
        Add(ref total, mapped);
        Add(ref total, graphics);
        Add(ref total, untracked);
        Add(ref total, androidRuntime);

        ApplyTargetStatHeuristics(decoded, ref total, ref graphics, ref untracked);

        // Move the VM root out of Native into Managed (Allocated Memory Distribution), mirroring the builder.
        var vmRootCommitted = vmRoot.Committed;
        var vmRootResident = vmRoot.Resident;
        if (vmRootId != long.MinValue)
        {
            managed.Committed += vmRootCommitted;
            managed.Resident += vmRootResident;
            native.Committed -= Math.Min(native.Committed, vmRootCommitted);
            native.Resident -= Math.Min(native.Resident, vmRootResident);
        }

        var result = new SummaryMetrics
        {
            // Total committed comes from the address-spectrum total (or legacy fallback); graphics/untracked
            // shuffling preserves it. Total resident is the resident of EVERY flattened span (including the
            // Graphics and Untracked regions), matching ResidentMemorySummaryModelBuilder — not just the rows
            // whose per-category resident is surfaced in the UI.
            TotalAllocatedBytes = total.Committed,
            TotalResidentBytes = total.Resident,
        };

        result.AllocatedMemoryDistribution.Add(Category(CategoryNative, native, true));
        result.AllocatedMemoryDistribution.Add(Category(CategoryManaged, managed, true));
        result.AllocatedMemoryDistribution.Add(Category(CategoryExecutablesAndMapped, mapped, true));
        result.AllocatedMemoryDistribution.Add(Category(CategoryGraphics, graphics, false));
        result.AllocatedMemoryDistribution.Add(Category(CategoryUntracked, untracked, false));
        if (androidRuntime.Committed > 0)
            result.AllocatedMemoryDistribution.Add(Category("Android Runtime", androidRuntime, true));

        // Managed Heap Utilization: add the VM root to the Virtual Machine row, mirroring the builder.
        var virtualMachine = vmSection;
        virtualMachine.Committed += vmRootCommitted;
        virtualMachine.Resident += vmRootResident;

        result.ManagedHeapUtilization.Add(Category(CategoryVirtualMachine, virtualMachine, true));
        result.ManagedHeapUtilization.Add(Category(CategoryObjects, objects, true));
        result.ManagedHeapUtilization.Add(Category(CategoryEmptyHeapSpace, emptyHeapSpace, true));

        return result;
    }

    private static void AddManagedBreakdown(
        DecodedSnapshot decoded,
        AddressPoint cur,
        Mem span,
        ref Mem vmSection,
        ref Mem emptyHeapSpace,
        ref Mem objects)
    {
        switch (cur.Kind)
        {
            case SourceKind.ManagedHeapSection:
                if (cur.Index < decoded.ManagedHeapSectionTypes.Length &&
                    decoded.ManagedHeapSectionTypes[cur.Index] == ManagedHeapSectionKind.VirtualMachine)
                    Add(ref vmSection, span);
                else
                    Add(ref emptyHeapSpace, span);
                break;
            case SourceKind.ManagedObject:
                Add(ref objects, span);
                break;
        }
    }

    /// <summary>
    /// Legacy untracked/total fallback and graphics estimation, ported from
    /// <c>AllMemorySummaryModelBuilder.BuildSummary</c>.
    /// </summary>
    private static void ApplyTargetStatHeuristics(DecodedSnapshot decoded, ref Mem total, ref Mem graphics, ref Mem untracked)
    {
        var stats = decoded.TargetMemoryStats;
        if (stats == null)
            return;

        var hasSystemRegions = decoded.SystemMemoryRegionAddresses.Length > 0;
        if (!hasSystemRegions && stats.TotalVirtualMemory > 0)
        {
            untracked = new Mem
            {
                Committed = stats.TotalVirtualMemory > total.Committed ? stats.TotalVirtualMemory - total.Committed : 0,
            };
            total = new Mem { Committed = stats.TotalVirtualMemory };
        }

        if (graphics.Committed < stats.GraphicsUsedMemory)
        {
            // System regions under-report graphics; reassign from untracked (capped by what untracked has).
            var delta = Math.Min(stats.GraphicsUsedMemory - graphics.Committed, untracked.Committed);
            untracked = new Mem { Committed = untracked.Committed - delta };
            graphics = new Mem { Committed = graphics.Committed + delta };
        }
        else
        {
            // Note: consoles with UseDeviceMemoryForGraphics fully track GPU memory and skip this branch.
            // The tool does not yet decode native-region GPU allocator indices, so we always apply it.
            var untrackedGraphics = graphics.Committed - stats.GraphicsUsedMemory;
            untracked = new Mem { Committed = untracked.Committed + untrackedGraphics };
            graphics = new Mem { Committed = graphics.Committed - untrackedGraphics };
        }
    }

    /// <summary>
    /// Root reference id of the native allocation or native object owning a flattened span, or null for
    /// other source kinds. Used to attribute spans to the VM root.
    /// </summary>
    private static long? SpanRootReferenceId(DecodedSnapshot decoded, AddressPoint point)
    {
        switch (point.Kind)
        {
            case SourceKind.NativeAllocation:
                return point.Index < decoded.NativeAllocationRootReferenceIds.Length
                    ? decoded.NativeAllocationRootReferenceIds[point.Index]
                    : null;
            case SourceKind.NativeObject:
                return point.Index < decoded.NativeObjectRootReferenceIds.Length
                    ? decoded.NativeObjectRootReferenceIds[point.Index]
                    : null;
            default:
                return null;
        }
    }

    private static int FindVmRootIndex(DecodedSnapshot decoded)
    {
        for (var i = 0; i < decoded.NativeRootObjectNames.Length; i++)
        {
            var name = decoded.NativeRootObjectNames[i];
            if (string.IsNullOrEmpty(name))
                continue;

            foreach (var vmRootName in VmRootNames)
            {
                if (string.Equals(name, vmRootName, StringComparison.Ordinal))
                    return i;
            }
        }

        return -1;
    }

    private static PointType GetPointType(DecodedSnapshot decoded, AddressPoint point, bool isAndroid)
    {
        switch (point.Kind)
        {
            case SourceKind.None:
                return PointType.Free;

            case SourceKind.SystemMemoryRegion:
            {
                if (isAndroid && point.Index < decoded.SystemMemoryRegionNames.Length)
                {
                    var name = decoded.SystemMemoryRegionNames[point.Index] ?? string.Empty;
                    if (name.StartsWith("[anon:dalvik-", StringComparison.Ordinal) ||
                        name.StartsWith("/dev/ashmem/dalvik-", StringComparison.Ordinal))
                        return PointType.AndroidRuntime;
                    if (name.StartsWith("/dev/", StringComparison.Ordinal))
                        return PointType.Device;
                }

                var regionType = point.Index < decoded.SystemMemoryRegionTypes.Length
                    ? (SystemRegionMemoryType)decoded.SystemMemoryRegionTypes[point.Index]
                    : SystemRegionMemoryType.Private;
                return regionType switch
                {
                    SystemRegionMemoryType.Device => PointType.Device,
                    SystemRegionMemoryType.Mapped => PointType.Mapped,
                    SystemRegionMemoryType.Shared => PointType.Shared,
                    _ => PointType.Untracked,
                };
            }

            case SourceKind.NativeMemoryRegion:
                return PointType.NativeReserved;

            case SourceKind.NativeAllocation:
            case SourceKind.NativeObject:
                return PointType.Native;

            case SourceKind.ManagedHeapSection:
                return point.Index < decoded.ManagedHeapSectionTypes.Length &&
                       decoded.ManagedHeapSectionTypes[point.Index] == ManagedHeapSectionKind.VirtualMachine
                    ? PointType.Managed
                    : PointType.ManagedReserved;

            case SourceKind.ManagedObject:
                return PointType.Managed;

            default:
                return PointType.Free;
        }
    }

    private static AddressPoint[] BuildSortedPoints(DecodedSnapshot decoded, IReadOnlyList<ManagedObjectRow> managedObjects)
    {
        var points = new List<AddressPoint>(
            (decoded.SystemMemoryRegionAddresses.Length +
             decoded.NativeMemoryRegionAddressBases.Length +
             decoded.ManagedHeapSectionStartAddresses.Length +
             decoded.NativeAllocationAddresses.Length +
             decoded.NativeObjectAddresses.Length +
             managedObjects.Count) * 2);

        var pairId = 0L;

        for (var i = 0; i < decoded.SystemMemoryRegionAddresses.Length; i++)
            AddPair(points, ref pairId, SourceKind.SystemMemoryRegion, i, decoded.SystemMemoryRegionAddresses[i], decoded.SystemMemoryRegionSizes[i]);

        for (var i = 0; i < decoded.NativeMemoryRegionAddressBases.Length; i++)
        {
            var address = decoded.NativeMemoryRegionAddressBases[i];
            var size = decoded.NativeMemoryRegionAddressSizes[i];
            var name = i < decoded.NativeMemoryRegionNames.Length ? decoded.NativeMemoryRegionNames[i] ?? string.Empty : string.Empty;
            // Exclude "virtual" allocators which report non-committed memory (matches Memory Profiler).
            if (address == 0 || name.Contains("Virtual Memory", StringComparison.Ordinal))
                continue;
            AddPair(points, ref pairId, SourceKind.NativeMemoryRegion, i, address, size);
        }

        for (var i = 0; i < decoded.ManagedHeapSectionStartAddresses.Length; i++)
        {
            var size = i < decoded.ManagedHeapSectionBytes.Length ? (ulong)decoded.ManagedHeapSectionBytes[i].Length : 0;
            AddPair(points, ref pairId, SourceKind.ManagedHeapSection, i, decoded.ManagedHeapSectionStartAddresses[i], size);
        }

        for (var i = 0; i < decoded.NativeAllocationAddresses.Length; i++)
            AddPair(points, ref pairId, SourceKind.NativeAllocation, i, decoded.NativeAllocationAddresses[i], decoded.NativeAllocationSizes[i]);

        for (var i = 0; i < decoded.NativeObjectAddresses.Length; i++)
            AddPair(points, ref pairId, SourceKind.NativeObject, i, decoded.NativeObjectAddresses[i], decoded.NativeObjectSizes[i]);

        for (var i = 0; i < managedObjects.Count; i++)
        {
            var row = managedObjects[i];
            AddPair(points, ref pairId, SourceKind.ManagedObject, i, row.Address, row.SizeBytes > 0 ? (ulong)row.SizeBytes : 0);
        }

        var array = points.ToArray();
        Array.Sort(array, Compare);
        return array;
    }

    private static void AddPair(List<AddressPoint> points, ref long pairId, SourceKind kind, int index, ulong address, ulong size)
    {
        if (size == 0)
            return;

        var id = pairId++;
        points.Add(new AddressPoint { Address = address, PairId = id, Kind = kind, Index = index, IsEnd = false });
        points.Add(new AddressPoint { Address = address + size, PairId = id, Kind = kind, Index = index, IsEnd = true });
    }

    /// <summary>
    /// Address-point ordering ported from <c>EntriesMemoryMapCache.AddressPoint.CompareTo</c>:
    /// by address, then end-before-start, then by source kind (descending for end points).
    /// </summary>
    private static int Compare(AddressPoint a, AddressPoint b)
    {
        var cmp = a.Address.CompareTo(b.Address);
        if (cmp != 0)
            return cmp;

        if (a.IsEnd != b.IsEnd)
            return a.IsEnd ? -1 : 1;

        var kindCmp = ((byte)a.Kind).CompareTo((byte)b.Kind);
        return a.IsEnd ? -kindCmp : kindCmp;
    }

    /// <summary>
    /// Resolves the owning source of every flattened span by walking the sorted points with a hierarchy
    /// stack, rewriting end points to continue their parent (or free space). Ported from
    /// <c>EntriesMemoryMapCache.PostProcess</c>; child-count bookkeeping is omitted as the flat scan only
    /// reads each point's resolved source.
    /// </summary>
    private static void PostProcess(AddressPoint[] points)
    {
        var stack = new List<int>(16);
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point.IsEnd)
            {
                if (stack.Count == 0)
                {
                    SetFree(ref points[i]);
                    continue;
                }

                var startIdx = stack[^1];
                if (points[startIdx].PairId != point.PairId)
                {
                    var level = FindStackLevel(points, stack, point.PairId);
                    if (level < 0)
                    {
                        // No matching start: treat as a continuation of the previous span.
                        points[i].Kind = points[i - 1].Kind;
                        points[i].Index = points[i - 1].Index;
                        continue;
                    }

                    stack.RemoveRange(level, stack.Count - level);
                }
                else
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                if (stack.Count > 0)
                {
                    var parent = points[stack[^1]];
                    points[i].Kind = parent.Kind;
                    points[i].Index = parent.Index;
                }
                else
                {
                    SetFree(ref points[i]);
                }
            }
            else
            {
                if (stack.Count > 0 && points[stack[^1]].Kind == point.Kind)
                {
                    // Same-type nesting indicates faulty/overlapping data; drop the enclosing point.
                    stack.RemoveAt(stack.Count - 1);
                }

                stack.Add(i);
            }
        }
    }

    private static int FindStackLevel(AddressPoint[] points, List<int> stack, long pairId)
    {
        for (var level = 0; level < stack.Count; level++)
        {
            if (points[stack[level]].PairId == pairId)
                return level;
        }

        return -1;
    }

    private static void SetFree(ref AddressPoint point)
    {
        point.Kind = SourceKind.None;
        point.Index = -1;
    }

    private static bool CanComputeResident(DecodedSnapshot decoded, int regionIndex) =>
        regionIndex >= 0 &&
        regionIndex < decoded.SystemMemoryResidentPageAddresses.Length &&
        regionIndex < decoded.SystemMemoryResidentPageFirstIndices.Length &&
        regionIndex < decoded.SystemMemoryResidentPageLastIndices.Length;

    private static void Add(ref Mem target, Mem value)
    {
        target.Committed += value.Committed;
        target.Resident += value.Resident;
    }

    private static SummaryCategory Category(string name, Mem mem, bool residentAvailable) =>
        new()
        {
            Name = name,
            CommittedBytes = mem.Committed,
            ResidentBytes = residentAvailable ? mem.Resident : 0,
            ResidentAvailable = residentAvailable,
        };
}

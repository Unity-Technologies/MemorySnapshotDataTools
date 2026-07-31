using System.Globalization;
using MemorySnapshotDataTools.Parser;

namespace MemorySnapshotDataTools.Parser;

/// <summary>
/// Bridge between raw Unity .snap file format and <see cref="RawSnapshotData"/>.
/// Reads a snapshot via <see cref="SnapReader"/>, decodes sections with <see cref="SnapSectionDecoders"/>,
/// then extracts native objects, managed heap objects, connections, roots, memory regions, and allocations.
/// </summary>
public static class SnapshotBridge
{
    /// <summary>
    /// Reads the snapshot from disk, decodes all sections, and extracts raw data into a <see cref="RawSnapshotData"/> instance.
    /// Reports progress via <paramref name="progress"/> and respects <paramref name="token"/> for cancellation.
    /// </summary>
    /// <param name="snapshotPath">Full path to the .snap file.</param>
    /// <param name="progress">Reporter for status messages during extraction.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>Fully populated raw snapshot data, validated in memory.</returns>
    /// <exception cref="OperationCanceledException">When <paramref name="token"/> is cancelled.</exception>
    public static RawSnapshotData ExtractRawData(string snapshotPath, IProgressReporter progress, CancellationToken token)
    {
        progress.Report("Reading snapshot sections...");
        using var reader = SnapReader.Open(snapshotPath);
        var decoded = SnapSectionDecoders.DecodeAll(reader);
        token.ThrowIfCancellationRequested();
        return ExtractFromDecoded(decoded, snapshotPath);
    }

    /// <summary>
    /// Extracts raw snapshot data from an already-decoded snapshot. Used by tests and by <see cref="ExtractRawData"/> after decoding.
    /// Populates native roots, memory regions, allocations, native objects, managed objects (via crawler), and connections, then validates.
    /// </summary>
    /// <param name="decoded">Decoded snapshot from <see cref="SnapSectionDecoders.DecodeAll"/>.</param>
    /// <param name="snapshotPath">Path to the source .snap file (stored in <see cref="SnapshotInfo"/>).</param>
    /// <returns>Validated <see cref="RawSnapshotData"/>.</returns>
    public static RawSnapshotData ExtractFromDecoded(DecodedSnapshot decoded, string snapshotPath)
    {
        var captureMeta = decoded.CaptureMetadata;
        var unityVersion = !string.IsNullOrWhiteSpace(captureMeta.UnityVersion)
            ? captureMeta.UnityVersion
            : $"format:{decoded.FormatVersion}";

        var data = new RawSnapshotData
        {
            SnapshotInfo = new SnapshotInfo
            {
                SnapshotPath = snapshotPath,
                ExportedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                UnityVersion = unityVersion,
                SnapFormatVersion = decoded.FormatVersion,
                SessionGuid = captureMeta.SessionGuid,
                ProductName = captureMeta.ProductName ?? string.Empty,
                Platform = captureMeta.Platform ?? string.Empty,
                RecordDateUtc = decoded.RecordDateTicksUtc > 0
                    ? new DateTime(decoded.RecordDateTicksUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
                    : string.Empty,
                PageSize = decoded.SystemMemoryResidentPageSize,
            }
        };

        ExtractNativeRoots(decoded, data.NativeRoots);
        ExtractMemoryRegions(decoded, data.MemoryRegions);
        ExtractNativeAllocations(decoded, data.NativeAllocations);
        ExtractSystemMemoryRegions(decoded, data.SystemMemoryRegions);

        var hasResident = ResidentMemoryCalculator.HasPerObjectResident(decoded);
        var hasSwapped = SwappedMemoryCalculator.HasPerObjectSwapped(decoded);
        var mapSizes = MemoryMapResidentAggregator.Compute(decoded);
        var rootIdToIndex = BuildRootIdToIndex(decoded);

        ExtractNativeObjects(decoded, data.NativeObjects, mapSizes, rootIdToIndex, hasResident);
        ApplyRootSizes(data.NativeRoots, mapSizes, hasResident);
        ApplySystemRegionSwappedSizes(decoded, data.SystemMemoryRegions, hasSwapped);
        var managedCrawl = ManagedSnapshotCrawler.Crawl(decoded);
        data.ManagedObjects.AddRange(managedCrawl.ManagedObjects);
        ExtractConnections(decoded, managedCrawl.ManagedConnections, data.Connections);
        data.SummaryMetrics = SummaryMetricsCalculator.Compute(decoded, data.ManagedObjects);
        ValidateStrictInMemory(data);
        return data;
    }

    private static void ExtractNativeRoots(DecodedSnapshot decoded, List<NativeRootRow> output)
    {
        output.Capacity = decoded.NativeRootIds.Length;
        for (var i = 0; i < decoded.NativeRootIds.Length; i++)
        {
            output.Add(new NativeRootRow
            {
                RootIndex = i,
                RootId = decoded.NativeRootIds[i],
                AreaName = decoded.NativeRootAreaNames[i] ?? string.Empty,
                ObjectName = decoded.NativeRootObjectNames[i] ?? string.Empty,
                AccumulatedSizeBytes = decoded.NativeRootAccumulatedSizes[i],
            });
        }
    }

    private static Dictionary<long, int> BuildRootIdToIndex(DecodedSnapshot decoded)
    {
        var map = new Dictionary<long, int>(decoded.NativeRootIds.Length);
        for (var i = 0; i < decoded.NativeRootIds.Length; i++)
            map[decoded.NativeRootIds[i]] = i;
        return map;
    }

    private static void ExtractNativeObjects(
        DecodedSnapshot decoded,
        List<NativeObjectRow> output,
        MemoryMapResidentAggregator.MemoryMapSizes mapSizes,
        Dictionary<long, int> rootIdToIndex,
        bool hasResident)
    {
        var rootResidentSizes = mapSizes.RootResidentSizes;
        var rootSwappedSizes = mapSizes.RootSwappedSizes;
        output.Capacity = decoded.NativeObjectNames.Length;
        for (var i = 0; i < decoded.NativeObjectNames.Length; i++)
        {
            var typeIndex = decoded.NativeObjectTypeIndices[i];
            var address = i < decoded.NativeObjectAddresses.Length ? decoded.NativeObjectAddresses[i] : 0UL;
            var rootReferenceId = i < decoded.NativeObjectRootReferenceIds.Length
                ? decoded.NativeObjectRootReferenceIds[i]
                : -1L;
            ulong? residentSizeBytes = null;
            ulong? swappedSizeBytes = null;
            if (rootReferenceId >= 1 && rootIdToIndex.TryGetValue(rootReferenceId, out var rootIndex))
            {
                if (hasResident && rootIndex < rootResidentSizes.Length)
                    residentSizeBytes = rootResidentSizes[rootIndex];
                if (rootSwappedSizes != null && rootIndex < rootSwappedSizes.Length)
                    swappedSizeBytes = rootSwappedSizes[rootIndex];
            }

            output.Add(new NativeObjectRow
            {
                NativeObjectIndex = i,
                InstanceId = decoded.NativeObjectInstanceIds[i].ToString(CultureInfo.InvariantCulture),
                Name = decoded.NativeObjectNames[i] ?? string.Empty,
                SizeBytes = decoded.NativeObjectSizes[i],
                NativeObjectAddress = address,
                RootReferenceId = rootReferenceId,
                ResidentSizeBytes = residentSizeBytes,
                SwappedSizeBytes = swappedSizeBytes,
                TypeIndex = typeIndex,
                NativeTypeName = typeIndex >= 0 && typeIndex < decoded.NativeTypeNames.Length
                    ? decoded.NativeTypeNames[typeIndex] ?? string.Empty
                    : string.Empty,
                IsDestroyed = i < decoded.NativeObjectFlags.Length && (decoded.NativeObjectFlags[i] & 0x8) != 0,
            });
        }
    }

    private static void ApplyRootSizes(List<NativeRootRow> roots, MemoryMapResidentAggregator.MemoryMapSizes mapSizes, bool hasResident)
    {
        var rootResidentSizes = mapSizes.RootResidentSizes;
        var rootSwappedSizes = mapSizes.RootSwappedSizes;
        if (!hasResident && rootSwappedSizes == null)
            return;

        for (var i = 0; i < roots.Count; i++)
        {
            var row = roots[i];
            if (hasResident)
                row.ResidentSizeBytes = i < rootResidentSizes.Length ? rootResidentSizes[i] : 0UL;
            if (rootSwappedSizes != null)
                row.SwappedSizeBytes = i < rootSwappedSizes.Length ? rootSwappedSizes[i] : 0UL;
            roots[i] = row;
        }
    }

    private static void ApplySystemRegionSwappedSizes(
        DecodedSnapshot decoded,
        List<SystemMemoryRegionRow> regions,
        bool hasSwapped)
    {
        if (!hasSwapped)
            return;

        var swappedPerRegion = SwappedMemoryCalculator.ComputePerSystemRegion(decoded);
        for (var i = 0; i < regions.Count; i++)
        {
            var row = regions[i];
            row.SwappedBytes = i < swappedPerRegion.Length ? swappedPerRegion[i] : 0UL;
            regions[i] = row;
        }
    }

    private static void ExtractSystemMemoryRegions(DecodedSnapshot decoded, List<SystemMemoryRegionRow> output)
    {
        output.Capacity = decoded.SystemMemoryRegionAddresses.Length;
        for (var i = 0; i < decoded.SystemMemoryRegionAddresses.Length; i++)
        {
            output.Add(new SystemMemoryRegionRow
            {
                RegionIndex = i,
                Address = decoded.SystemMemoryRegionAddresses[i],
                SizeBytes = i < decoded.SystemMemoryRegionSizes.Length ? decoded.SystemMemoryRegionSizes[i] : 0,
                ResidentBytes = i < decoded.SystemMemoryRegionResidentSizes.Length ? decoded.SystemMemoryRegionResidentSizes[i] : 0,
                Type = i < decoded.SystemMemoryRegionTypes.Length ? decoded.SystemMemoryRegionTypes[i] : 0,
                Name = i < decoded.SystemMemoryRegionNames.Length ? decoded.SystemMemoryRegionNames[i] ?? string.Empty : string.Empty,
            });
        }
    }

    private static void ExtractMemoryRegions(DecodedSnapshot decoded, List<MemoryRegionRow> output)
    {
        output.Capacity = decoded.NativeMemoryRegionAddressBases.Length;
        for (var i = 0; i < decoded.NativeMemoryRegionAddressBases.Length; i++)
        {
            output.Add(new MemoryRegionRow
            {
                RegionIndex = i,
                AddressBase = decoded.NativeMemoryRegionAddressBases[i],
                AddressSize = decoded.NativeMemoryRegionAddressSizes[i],
                Name = decoded.NativeMemoryRegionNames[i] ?? string.Empty,
                ParentRegionIndex = decoded.NativeMemoryRegionParentIndices[i],
                FirstAllocationIndex = decoded.NativeMemoryRegionFirstAllocationIndices[i],
                NumAllocations = decoded.NativeMemoryRegionNumAllocations[i],
            });
        }
    }

    private static void ExtractNativeAllocations(DecodedSnapshot decoded, List<NativeAllocationRow> output)
    {
        output.Capacity = decoded.NativeAllocationAddresses.Length;
        for (var i = 0; i < decoded.NativeAllocationAddresses.Length; i++)
        {
            output.Add(new NativeAllocationRow
            {
                AllocationIndex = i,
                Address = decoded.NativeAllocationAddresses[i],
                SizeBytes = decoded.NativeAllocationSizes[i],
                OverheadSizeBytes = decoded.NativeAllocationOverheadSizes[i],
                PaddingSizeBytes = decoded.NativeAllocationPaddingSizes[i],
                MemoryRegionIndex = decoded.NativeAllocationMemoryRegionIndices[i],
                RootReferenceId = i < decoded.NativeAllocationRootReferenceIds.Length
                    ? decoded.NativeAllocationRootReferenceIds[i]
                    : -1,
            });
        }
    }

    private static void ExtractConnections(DecodedSnapshot decoded, List<ConnectionRow> managedConnections, List<ConnectionRow> output)
    {
        var dedupe = new HashSet<ConnectionKey>();
        var gcHandleUniqueCount = decoded.GcHandleTargets.Length;
        var count = decoded.ConnectionsFrom.Length;
        output.Capacity = count + managedConnections.Count;
        for (var i = 0; i < count; i++)
        {
            var fromSource = MapUnifiedIndexToSource(decoded.ConnectionsFrom[i], gcHandleUniqueCount);
            var toSource = MapUnifiedIndexToSource(decoded.ConnectionsTo[i], gcHandleUniqueCount);

            var row = new ConnectionRow
            {
                FromKind = fromSource.Kind,
                FromIndex = fromSource.Index,
                ToKind = toSource.Kind,
                ToIndex = toSource.Index,
                ConnectionType = "native_connection",
            };
            AddConnectionIfNew(output, dedupe, row);
        }

        for (var i = 0; i < managedConnections.Count; i++)
            AddConnectionIfNew(output, dedupe, managedConnections[i]);
    }

    private static SourceRef MapUnifiedIndexToSource(int unifiedIndex, int gcHandleUniqueCount)
        => unifiedIndex < 0
            ? new SourceRef("unknown", unifiedIndex)
            : unifiedIndex < gcHandleUniqueCount
            ? new SourceRef("managed_object", unifiedIndex)
            : new SourceRef("native_object", unifiedIndex - gcHandleUniqueCount);

    private readonly struct SourceRef(string kind, long index)
    {
        public string Kind { get; } = kind;
        public long Index { get; } = index;
    }

    private static void AddConnectionIfNew(
        List<ConnectionRow> output,
        HashSet<ConnectionKey> dedupe,
        ConnectionRow row)
    {
        var key = new ConnectionKey(row.FromKind, row.FromIndex, row.ToKind, row.ToIndex, row.ConnectionType);
        if (dedupe.Add(key))
            output.Add(row);
    }

    private readonly record struct ConnectionKey(
        string FromKind,
        long FromIndex,
        string ToKind,
        long ToIndex,
        string ConnectionType);

    private static void ValidateStrictInMemory(RawSnapshotData data)
    {
        for (var i = 0; i < data.ManagedObjects.Count; i++)
        {
            var row = data.ManagedObjects[i];
            if (row.ManagedObjectIndex != i)
                throw new InvalidOperationException($"Managed object index mismatch. expected={i}, actual={row.ManagedObjectIndex}");
            if (row.Address == 0)
                throw new InvalidOperationException($"Managed object {i} has null address.");
            if (row.SizeBytes <= 0)
                throw new InvalidOperationException($"Managed object {i} has non-positive size {row.SizeBytes}.");
            if (row.TypeIndex < 0 || string.IsNullOrWhiteSpace(row.ManagedTypeName))
                throw new InvalidOperationException($"Managed object {i} has unresolved managed type metadata.");
            if (row.NativeObjectIndex < -1 || row.NativeObjectIndex >= data.NativeObjects.Count)
                throw new InvalidOperationException($"Managed object {i} has invalid native_object_index {row.NativeObjectIndex}.");
        }

        for (var i = 0; i < data.NativeObjects.Count; i++)
        {
            var row = data.NativeObjects[i];
            if (row.NativeObjectIndex != i)
                throw new InvalidOperationException($"Native object index mismatch. expected={i}, actual={row.NativeObjectIndex}");
        }

        for (var i = 0; i < data.MemoryRegions.Count; i++)
        {
            var row = data.MemoryRegions[i];
            if (row.RegionIndex != i)
                throw new InvalidOperationException($"Memory region index mismatch. expected={i}, actual={row.RegionIndex}");
            if (row.ParentRegionIndex >= data.MemoryRegions.Count)
                throw new InvalidOperationException($"Memory region {i} has invalid parent_region_index {row.ParentRegionIndex}.");
            if (row.FirstAllocationIndex >= data.NativeAllocations.Count)
                throw new InvalidOperationException($"Memory region {i} has invalid first_allocation_index {row.FirstAllocationIndex}.");
            if (row.NumAllocations < 0)
                throw new InvalidOperationException($"Memory region {i} has negative num_allocations {row.NumAllocations}.");
        }

        for (var i = 0; i < data.NativeAllocations.Count; i++)
        {
            var row = data.NativeAllocations[i];
            if (row.AllocationIndex != i)
                throw new InvalidOperationException($"Native allocation index mismatch. expected={i}, actual={row.AllocationIndex}");
            if (row.MemoryRegionIndex >= data.MemoryRegions.Count)
                throw new InvalidOperationException($"Native allocation {i} has invalid memory_region_index {row.MemoryRegionIndex}.");
        }

        for (var i = 0; i < data.Connections.Count; i++)
        {
            var c = data.Connections[i];
            ValidateEndpoint(c.FromKind, c.FromIndex, data, $"connections[{i}].from");
            ValidateEndpoint(c.ToKind, c.ToIndex, data, $"connections[{i}].to");
        }
    }

    private static void ValidateEndpoint(string kind, long index, RawSnapshotData data, string label)
    {
        if (kind == "managed_object")
        {
            if (index < 0 || index >= data.ManagedObjects.Count)
                throw new InvalidOperationException($"{label} points to out-of-range managed object index {index}.");
            return;
        }

        if (kind == "native_object")
        {
            if (index < 0 || index >= data.NativeObjects.Count)
                throw new InvalidOperationException($"{label} points to out-of-range native object index {index}.");
            return;
        }

        throw new InvalidOperationException($"{label} has unsupported endpoint kind '{kind}'.");
    }
}

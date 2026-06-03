using MemorySnapshotDataTools;
using MemorySnapshotDataTools.Parser;
using Xunit;

namespace MemorySnapshotDataTools.Tests;

/// <summary>
/// Integration tests for resident memory export via <see cref="SnapshotBridge"/>.
/// </summary>
public sealed class ResidentMemoryCalculatorTests
{
    /// <summary>
    /// Verifies resident size is exported for format 17+ snapshots with page bitmap data.
    /// </summary>
    [Fact]
    public void ExtractFromDecoded_Format17_ExportsResidentSizeBytes()
    {
        const ulong regionBase = 0x1000;
        const ulong pageSize = 4096;

        var decoded = CreateMinimalDecoded(formatVersion: 17);
        decoded.SystemMemoryResidentPageAddresses = [regionBase];
        decoded.SystemMemoryResidentPageFirstIndices = [0];
        decoded.SystemMemoryResidentPageLastIndices = [0];
        decoded.SystemMemoryResidentPageSize = pageSize;
        decoded.SystemMemoryResidentPageStates = [new byte[] { 0b0000_0001 }];
        decoded.SystemMemoryRegionAddresses = [regionBase];
        decoded.SystemMemoryRegionSizes = [pageSize];
        decoded.SystemMemoryRegionResidentSizes = [pageSize];

        decoded.NativeObjectAddresses = [regionBase];
        decoded.NativeObjectSizes = [pageSize];
        decoded.NativeObjectRootReferenceIds = [1];
        decoded.NativeRootIds = [1];
        decoded.NativeRootAreaNames = ["root"];
        decoded.NativeRootObjectNames = ["obj"];
        decoded.NativeRootAccumulatedSizes = [pageSize];

        var data = SnapshotBridge.ExtractFromDecoded(decoded, "/test.snap");
        var row = Assert.Single(data.NativeObjects);
        Assert.Equal(pageSize, row.ResidentSizeBytes);
        // page_size is carried from the decoded snapshot into snapshot_info.
        Assert.Equal(pageSize, data.SnapshotInfo.PageSize);
    }

    /// <summary>
    /// Format versions below 17 leave resident_size_bytes null on native objects.
    /// </summary>
    [Fact]
    public void ExtractFromDecoded_Format16_ResidentSizeIsNull()
    {
        var decoded = CreateMinimalDecoded(formatVersion: 16);
        decoded.SystemMemoryRegionAddresses = [0x1000];
        decoded.SystemMemoryRegionSizes = [8192];
        decoded.SystemMemoryRegionResidentSizes = [4096];
        decoded.NativeObjectAddresses = [0x1000];
        decoded.NativeObjectSizes = [4096];

        var data = SnapshotBridge.ExtractFromDecoded(decoded, "/test.snap");
        var row = Assert.Single(data.NativeObjects);
        Assert.Null(row.ResidentSizeBytes);
        Assert.Single(data.SystemMemoryRegions);
        // No resident page data (format < 17) → page_size unknown (0).
        Assert.Equal(0UL, data.SnapshotInfo.PageSize);
    }

    private static DecodedSnapshot CreateMinimalDecoded(uint formatVersion)
    {
        return new DecodedSnapshot
        {
            FormatVersion = formatVersion,
            NativeObjectTypeIndices = [0],
            NativeObjectInstanceIds = [1UL],
            NativeObjectNames = ["obj"],
            NativeTypeNames = ["GameObject"],
            NativeObjectSizes = [],
            NativeObjectAddresses = [],
            NativeObjectRootReferenceIds = [],
            NativeObjectFlags = [],
            NativeObjectGcHandleIndices = [-1],
            GcHandleTargets = [],
            ConnectionsFrom = [],
            ConnectionsTo = [],
            NativeRootIds = [],
            NativeRootAreaNames = [],
            NativeRootObjectNames = [],
            NativeRootAccumulatedSizes = [],
            NativeMemoryRegionNames = [],
            NativeMemoryRegionParentIndices = [],
            NativeMemoryRegionAddressBases = [],
            NativeMemoryRegionAddressSizes = [],
            NativeMemoryRegionFirstAllocationIndices = [],
            NativeMemoryRegionNumAllocations = [],
            NativeAllocationAddresses = [],
            NativeAllocationSizes = [],
            NativeAllocationOverheadSizes = [],
            NativeAllocationPaddingSizes = [],
            NativeAllocationMemoryRegionIndices = [],
            NativeAllocationRootReferenceIds = [],
            VirtualMachineInformation = new DecodedVirtualMachineInfo { PointerSize = 8 },
            ManagedHeapSectionStartAddresses = [],
            ManagedHeapSectionBytes = [],
            ManagedTypeFlags = [],
            ManagedTypeNames = [],
            ManagedTypeAssemblies = [],
            ManagedTypeBaseOrElementTypeIndices = [],
            ManagedTypeSizes = [],
            ManagedTypeInfoAddresses = [],
            ManagedTypeFieldIndices = [],
            FieldOffsets = [],
            FieldTypeIndices = [],
            FieldNames = [],
            FieldIsStatic = [],
        };
    }
}

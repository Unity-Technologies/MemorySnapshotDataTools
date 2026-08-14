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

    /// <summary>
    /// Per-region resident calculation over a synthetic 4-page residency bitmap, exercised through the
    /// public export path (<see cref="SnapshotBridge.ExtractFromDecoded"/>, which calls
    /// <c>ResidentMemoryCalculator.ComputePerRegion</c>). Verifies exact byte counts including trimmed
    /// partial head/tail pages, plus the size==0 (null) and no-coverage (0) edge cases.
    /// </summary>
    [Fact]
    public void ExtractFromDecoded_MemoryRegions_TrimsPartialPagesAndHandlesEdgeCases()
    {
        const ulong pageSize = 4096;
        const ulong residentBase = 0x10000; // base address the resident-page range starts at

        var decoded = CreateMinimalDecoded(formatVersion: 17);
        decoded.NativeObjectSizes = [0]; // one native object exists (minimal decoded); give it a size

        // 4 pages (indices 0..3) starting at residentBase. Pages 0,1,3 resident; page 2 not resident.
        // bit0=page0=1, bit1=page1=1, bit2=page2=0, bit3=page3=1  ->  0b0000_1011 = 0x0B
        decoded.SystemMemoryResidentPageAddresses = [residentBase];
        decoded.SystemMemoryResidentPageFirstIndices = [0];
        decoded.SystemMemoryResidentPageLastIndices = [3];
        decoded.SystemMemoryResidentPageSize = pageSize;
        decoded.SystemMemoryResidentPageStates = [new byte[] { 0b0000_1011 }];

        // Region 0: page-aligned, exactly one fully-resident page -> no trimming -> 4096.
        // Region 1: starts mid-page-0 (offset 2048) and ends mid-page-3, spanning pages 0..3.
        //           pages 0(res),1(res),2(not),3(res). Covered bytes per page:
        //           page0 2048 (head-trimmed), page1 4096, page2 0 (not resident), page3 2048 (tail-trimmed)
        //           => 2048 + 4096 + 0 + 2048 = 8192.
        // Region 2: base below the resident-page range -> no covering range -> 0 (known, not unknown).
        // Region 3: size == 0 -> null (no meaningful resident value).
        decoded.NativeMemoryRegionAddressBases =
        [
            residentBase,          // region 0
            residentBase + 2048,   // region 1
            0x100,                 // region 2 (below residentBase)
            0x20000,               // region 3
        ];
        decoded.NativeMemoryRegionAddressSizes =
        [
            pageSize, // region 0
            12288,    // region 1 (spans pages 0..3, mid-page start and end)
            pageSize, // region 2
            0,        // region 3
        ];
        decoded.NativeMemoryRegionNames = ["r0", "r1", "r2", "r3"];
        decoded.NativeMemoryRegionParentIndices = [-1, -1, -1, -1];
        decoded.NativeMemoryRegionFirstAllocationIndices = [-1, -1, -1, -1];
        decoded.NativeMemoryRegionNumAllocations = [0, 0, 0, 0];

        var data = SnapshotBridge.ExtractFromDecoded(decoded, "/test.snap");

        Assert.Equal(4, data.MemoryRegions.Count);
        Assert.Equal(4096UL, data.MemoryRegions[0].ResidentSizeBytes);   // aligned full page
        Assert.Equal(8192UL, data.MemoryRegions[1].ResidentSizeBytes);   // head+tail trimmed, middle page not resident
        Assert.Equal(0UL, data.MemoryRegions[2].ResidentSizeBytes);      // no covering resident-page range
        Assert.Null(data.MemoryRegions[3].ResidentSizeBytes);            // size == 0 -> unknown/null
    }

    /// <summary>
    /// A snapshot with no residency bitmap (format &lt; 17) yields all-null per-region resident sizes,
    /// so the exported column is NULL ("unknown") rather than a misleading 0.
    /// </summary>
    [Fact]
    public void ExtractFromDecoded_MemoryRegions_NoBitmap_ResidentSizeIsNull()
    {
        var decoded = CreateMinimalDecoded(formatVersion: 16);
        decoded.NativeObjectSizes = [0]; // one native object exists (minimal decoded); give it a size
        decoded.NativeMemoryRegionAddressBases = [0x10000, 0x20000];
        decoded.NativeMemoryRegionAddressSizes = [4096, 4096];
        decoded.NativeMemoryRegionNames = ["r0", "r1"];
        decoded.NativeMemoryRegionParentIndices = [-1, -1];
        decoded.NativeMemoryRegionFirstAllocationIndices = [-1, -1];
        decoded.NativeMemoryRegionNumAllocations = [0, 0];

        var data = SnapshotBridge.ExtractFromDecoded(decoded, "/test.snap");

        Assert.Equal(2, data.MemoryRegions.Count);
        Assert.All(data.MemoryRegions, r => Assert.Null(r.ResidentSizeBytes));
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

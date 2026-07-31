using MemorySnapshotDataTools;
using MemorySnapshotDataTools.Parser;
using Xunit;

namespace MemorySnapshotDataTools.Tests;

/// <summary>
/// Tests for swapped-page math (SwappedMemoryCalculator) via the public <see cref="SnapshotBridge"/>
/// surface, mirroring <see cref="ResidentMemoryCalculatorTests"/>. Swapped-page entries (93–97) are
/// optional at format v17; availability is gated on entry presence, never on format version.
/// </summary>
public sealed class SwappedMemoryCalculatorTests
{
    private const ulong RegionBase = 0x1000;
    private const ulong PageSize = 4096;

    /// <summary>
    /// Known bitset: pages 1 and 2 of a 4-page range are swapped (0b0110); an object covering the
    /// full range reports exactly two swapped pages, and the system region reports the same.
    /// </summary>
    [Fact]
    public void ExtractFromDecoded_KnownBitset_CountsSwappedPages()
    {
        var decoded = CreateDecodedWithSwappedPages(pageCount: 4, swappedStates: [0b0000_0110]);
        decoded.NativeObjectAddresses = [RegionBase];
        decoded.NativeObjectSizes = [4 * PageSize];

        var data = SnapshotBridge.ExtractFromDecoded(decoded, "/test.snap");

        Assert.Equal(2 * PageSize, Assert.Single(data.NativeObjects).SwappedSizeBytes);
        Assert.Equal(2 * PageSize, Assert.Single(data.NativeRoots).SwappedSizeBytes);
        Assert.Equal(2 * PageSize, Assert.Single(data.SystemMemoryRegions).SwappedBytes);
    }

    /// <summary>
    /// An object starting 100 bytes into a swapped boundary page has the partial head trimmed
    /// (page 0 swapped, page 1 not: only the remainder of page 0 counts).
    /// </summary>
    [Fact]
    public void ExtractFromDecoded_PartialHeadPage_TrimsHead()
    {
        var decoded = CreateDecodedWithSwappedPages(pageCount: 2, swappedStates: [0b0000_0001]);
        decoded.NativeObjectAddresses = [RegionBase + 100];
        decoded.NativeObjectSizes = [PageSize];

        var data = SnapshotBridge.ExtractFromDecoded(decoded, "/test.snap");

        Assert.Equal(PageSize - 100, Assert.Single(data.NativeObjects).SwappedSizeBytes);
    }

    /// <summary>
    /// An object ending 100 bytes into a swapped boundary page has the partial tail trimmed
    /// (both pages swapped, object covers page 0 plus 100 bytes of page 1).
    /// </summary>
    [Fact]
    public void ExtractFromDecoded_PartialTailPage_TrimsTail()
    {
        var decoded = CreateDecodedWithSwappedPages(pageCount: 2, swappedStates: [0b0000_0011]);
        decoded.NativeObjectAddresses = [RegionBase];
        decoded.NativeObjectSizes = [PageSize + 100];

        var data = SnapshotBridge.ExtractFromDecoded(decoded, "/test.snap");

        Assert.Equal(PageSize + 100, Assert.Single(data.NativeObjects).SwappedSizeBytes);
    }

    /// <summary>
    /// Absent swapped entries (a v17 snapshot with resident pages only) leave every swapped
    /// field null: object rows, root rows, and system region rows.
    /// </summary>
    [Fact]
    public void ExtractFromDecoded_SwappedEntriesAbsent_SwappedSizesAreNull()
    {
        var decoded = CreateMinimalDecoded(formatVersion: 17);
        decoded.SystemMemoryResidentPageAddresses = [RegionBase];
        decoded.SystemMemoryResidentPageFirstIndices = [0];
        decoded.SystemMemoryResidentPageLastIndices = [0];
        decoded.SystemMemoryResidentPageSize = PageSize;
        decoded.SystemMemoryResidentPageStates = [new byte[] { 0b0000_0001 }];
        decoded.SystemMemoryRegionAddresses = [RegionBase];
        decoded.SystemMemoryRegionSizes = [PageSize];
        decoded.SystemMemoryRegionResidentSizes = [PageSize];

        decoded.NativeObjectAddresses = [RegionBase];
        decoded.NativeObjectSizes = [PageSize];
        decoded.NativeObjectRootReferenceIds = [1];
        decoded.NativeRootIds = [1];
        decoded.NativeRootAreaNames = ["root"];
        decoded.NativeRootObjectNames = ["obj"];
        decoded.NativeRootAccumulatedSizes = [PageSize];

        var data = SnapshotBridge.ExtractFromDecoded(decoded, "/test.snap");

        var objectRow = Assert.Single(data.NativeObjects);
        Assert.Equal(PageSize, objectRow.ResidentSizeBytes);
        Assert.Null(objectRow.SwappedSizeBytes);
        Assert.Null(Assert.Single(data.NativeRoots).SwappedSizeBytes);
        Assert.Null(Assert.Single(data.SystemMemoryRegions).SwappedBytes);
        Assert.False(data.SummaryMetrics.SwappedAvailable);
    }

    /// <summary>
    /// End-to-end: a v17 snapshot with both resident and swapped bitmaps exports both sizes.
    /// One of the region's two pages is resident and the other swapped (a page is never both),
    /// and the summary reports swapped as available with the correct total.
    /// </summary>
    [Fact]
    public void ExtractFromDecoded_ResidentAndSwapped_ExportsBothSizes()
    {
        var decoded = CreateDecodedWithSwappedPages(pageCount: 2, swappedStates: [0b0000_0010]);
        decoded.SystemMemoryResidentPageStates = [new byte[] { 0b0000_0001 }]; // page 0 resident
        decoded.NativeObjectAddresses = [RegionBase];
        decoded.NativeObjectSizes = [2 * PageSize];

        var data = SnapshotBridge.ExtractFromDecoded(decoded, "/test.snap");

        var objectRow = Assert.Single(data.NativeObjects);
        Assert.Equal(PageSize, objectRow.ResidentSizeBytes);
        Assert.Equal(PageSize, objectRow.SwappedSizeBytes);

        var rootRow = Assert.Single(data.NativeRoots);
        Assert.Equal(PageSize, rootRow.ResidentSizeBytes);
        Assert.Equal(PageSize, rootRow.SwappedSizeBytes);

        Assert.Equal(PageSize, Assert.Single(data.SystemMemoryRegions).SwappedBytes);

        Assert.True(data.SummaryMetrics.SwappedAvailable);
        Assert.Equal(PageSize, data.SummaryMetrics.TotalSwappedBytes);
    }

    /// <summary>
    /// One system region with resident and swapped page bitmaps covering <paramref name="pageCount"/>
    /// pages, one native object rooted to root id 1 (addresses/sizes set by each test).
    /// </summary>
    private static DecodedSnapshot CreateDecodedWithSwappedPages(int pageCount, byte[] swappedStates)
    {
        var decoded = CreateMinimalDecoded(formatVersion: 17);
        decoded.SystemMemoryResidentPageAddresses = [RegionBase];
        decoded.SystemMemoryResidentPageFirstIndices = [0];
        decoded.SystemMemoryResidentPageLastIndices = [pageCount - 1];
        decoded.SystemMemoryResidentPageSize = PageSize;
        decoded.SystemMemoryResidentPageStates = [new byte[] { 0 }];
        decoded.SystemMemorySwappedPageAddresses = [RegionBase];
        decoded.SystemMemorySwappedPageFirstIndices = [0];
        decoded.SystemMemorySwappedPageLastIndices = [pageCount - 1];
        decoded.SystemMemorySwappedPageSize = PageSize;
        decoded.SystemMemorySwappedPageStates = [swappedStates];
        decoded.SystemMemoryRegionAddresses = [RegionBase];
        decoded.SystemMemoryRegionSizes = [(ulong)pageCount * PageSize];
        decoded.SystemMemoryRegionResidentSizes = [0];

        decoded.NativeObjectRootReferenceIds = [1];
        decoded.NativeRootIds = [1];
        decoded.NativeRootAreaNames = ["root"];
        decoded.NativeRootObjectNames = ["obj"];
        decoded.NativeRootAccumulatedSizes = [PageSize];
        return decoded;
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

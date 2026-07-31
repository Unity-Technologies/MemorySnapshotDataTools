using MemorySnapshotDataTools;
using MemorySnapshotDataTools.Parser;
using Xunit;

namespace MemorySnapshotDataTools.Tests;

public sealed class SnapshotBridgeTests
{
    /// <summary>
    /// Builds a minimal DecodedSnapshot that passes ExtractFromDecoded validation:
    /// no managed objects, no connections, no memory regions/allocations.
    /// </summary>
    private static DecodedSnapshot CreateMinimalDecoded()
    {
        return new DecodedSnapshot
        {
            FormatVersion = 1,
            NativeTypeNames = [],
            NativeObjectTypeIndices = [],
            NativeObjectInstanceIds = [],
            NativeObjectNames = [],
            NativeObjectSizes = [],
            NativeObjectFlags = [],
            NativeObjectGcHandleIndices = [],
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

    [Fact]
    public void ExtractFromDecoded_MinimalNativeRoots_ProducesMatchingRows()
    {
        var decoded = CreateMinimalDecoded();
        decoded.NativeRootIds = [123L];
        decoded.NativeRootAreaNames = ["Scene"];
        decoded.NativeRootObjectNames = ["Root"];
        decoded.NativeRootAccumulatedSizes = [1000UL];

        var data = SnapshotBridge.ExtractFromDecoded(decoded, "/path/to/snap.snap");

        var row = Assert.Single(data.NativeRoots);
        Assert.Equal(0, row.RootIndex);
        Assert.Equal(123L, row.RootId);
        Assert.Equal("Scene", row.AreaName);
        Assert.Equal("Root", row.ObjectName);
        Assert.Equal(1000UL, row.AccumulatedSizeBytes);
    }

    [Fact]
    public void ExtractFromDecoded_MinimalNativeObjects_ProducesMatchingRows()
    {
        var decoded = CreateMinimalDecoded();
        decoded.NativeTypeNames = ["GameObject"];
        decoded.NativeObjectTypeIndices = [0];
        decoded.NativeObjectInstanceIds = [42UL];
        decoded.NativeObjectNames = ["MyGo"];
        decoded.NativeObjectSizes = [64UL];
        decoded.NativeObjectFlags = [0];
        decoded.NativeObjectAddresses = [0UL];
        decoded.NativeObjectRootReferenceIds = [-1L];

        var data = SnapshotBridge.ExtractFromDecoded(decoded, "/path/to/snap.snap");

        var row = Assert.Single(data.NativeObjects);
        Assert.Equal(0, row.NativeObjectIndex);
        Assert.Equal("42", row.InstanceId);
        Assert.Equal("MyGo", row.Name);
        Assert.Equal(64UL, row.SizeBytes);
        Assert.Equal(0UL, row.NativeObjectAddress);
        Assert.Null(row.ResidentSizeBytes);
        Assert.Null(row.SwappedSizeBytes);
        Assert.Equal(0, row.TypeIndex);
        Assert.Equal("GameObject", row.NativeTypeName);
        Assert.False(row.IsDestroyed);
    }
}

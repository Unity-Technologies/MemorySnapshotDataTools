using MemorySnapshotDataTools;
using MemorySnapshotDataTools.Parser;
using Xunit;

namespace MemorySnapshotDataTools.Tests;

/// <summary>
/// Tests for <see cref="SummaryMetricsCalculator"/> using a synthetic decoded snapshot with a known
/// address layout, so the address-spectrum flatten and category classification are deterministic.
/// </summary>
public sealed class SummaryMetricsCalculatorTests
{
    /// <summary>
    /// One private system region (0x1000..0x3000) overlaying a native allocation (Native) and a VM
    /// managed heap section (Managed / Virtual Machine); the rest of the region is Untracked.
    /// </summary>
    [Fact]
    public void Compute_OverlappingRegions_ClassifiesByInnermostSource()
    {
        var decoded = new DecodedSnapshot
        {
            FormatVersion = 16, // < 17: no resident pages, so resident sizes are 0.
            SystemMemoryRegionAddresses = [0x1000],
            SystemMemoryRegionSizes = [0x2000],
            SystemMemoryRegionResidentSizes = [0],
            SystemMemoryRegionTypes = [0], // Private -> Untracked
            SystemMemoryRegionNames = ["region"],
            NativeAllocationAddresses = [0x1400],
            NativeAllocationSizes = [0x400],
            ManagedHeapSectionStartAddresses = [0x2000],
            ManagedHeapSectionBytes = [new byte[0x400]],
            ManagedHeapSectionTypes = [ManagedHeapSectionKind.VirtualMachine],
        };

        var result = SummaryMetricsCalculator.Compute(decoded, []);

        Assert.Equal(8192UL, result.TotalAllocatedBytes);
        Assert.Equal(0UL, result.TotalResidentBytes);

        Assert.Equal(1024UL, Committed(result.AllocatedMemoryDistribution, "Native"));
        Assert.Equal(1024UL, Committed(result.AllocatedMemoryDistribution, "Managed"));
        Assert.Equal(0UL, Committed(result.AllocatedMemoryDistribution, "Executables & Mapped"));
        Assert.Equal(0UL, Committed(result.AllocatedMemoryDistribution, "Graphics (Estimated)"));
        Assert.Equal(6144UL, Committed(result.AllocatedMemoryDistribution, "Untracked"));

        Assert.Equal(1024UL, Committed(result.ManagedHeapUtilization, "Virtual Machine"));
        Assert.Equal(0UL, Committed(result.ManagedHeapUtilization, "Objects"));
        Assert.Equal(0UL, Committed(result.ManagedHeapUtilization, "Empty Heap Space"));

        // Graphics and Untracked report resident as unavailable.
        Assert.False(Row(result.AllocatedMemoryDistribution, "Graphics (Estimated)").ResidentAvailable);
        Assert.False(Row(result.AllocatedMemoryDistribution, "Untracked").ResidentAvailable);
    }

    /// <summary>
    /// The map-resolved size of the allocations rooted to the Mono/IL2CPP VM root moves from Native into
    /// Managed in the Allocated Memory Distribution, and forms the Virtual Machine row of the Managed Heap
    /// Utilization. Allocations rooted elsewhere stay in Native.
    /// </summary>
    [Fact]
    public void Compute_VmRoot_ReassignsNativeToManaged()
    {
        var decoded = new DecodedSnapshot
        {
            FormatVersion = 16,
            SystemMemoryRegionAddresses = [0x1000],
            SystemMemoryRegionSizes = [0x2000],
            SystemMemoryRegionResidentSizes = [0],
            SystemMemoryRegionTypes = [0],
            SystemMemoryRegionNames = ["region"],
            // Allocation A (rooted to the VM root id 1) and allocation B (rooted elsewhere).
            NativeAllocationAddresses = [0x1000, 0x1800],
            NativeAllocationSizes = [0x400, 0x400],
            NativeAllocationRootReferenceIds = [1, 5],
            NativeRootIds = [1],
            NativeRootObjectNames = ["Mono VM"],
            NativeRootAreaNames = ["VM"],
            NativeRootAccumulatedSizes = [9999], // raw accumulated size is intentionally ignored
        };

        var result = SummaryMetricsCalculator.Compute(decoded, []);

        // 2048 native total - 1024 (allocation A, rooted to VM) reassigned to managed.
        Assert.Equal(1024UL, Committed(result.AllocatedMemoryDistribution, "Native"));
        Assert.Equal(1024UL, Committed(result.AllocatedMemoryDistribution, "Managed"));
        Assert.Equal(1024UL, Committed(result.ManagedHeapUtilization, "Virtual Machine"));
    }

    private static SummaryCategory Row(IEnumerable<SummaryCategory> rows, string name) =>
        rows.Single(r => r.Name == name);

    private static ulong Committed(IEnumerable<SummaryCategory> rows, string name) =>
        Row(rows, name).CommittedBytes;
}

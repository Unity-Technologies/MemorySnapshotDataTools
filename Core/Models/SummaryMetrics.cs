namespace MemorySnapshotDataTools;

/// <summary>
/// MemoryProfiler "Summary" page metrics computed from a snapshot: the Allocated Memory Distribution
/// and Managed Heap Utilization breakdowns plus overall totals. Mirrors what the Unity Memory Profiler
/// shows so it can be validated against golden values.
/// </summary>
public sealed class SummaryMetrics
{
    /// <summary>Total committed (allocated) bytes across all categories.</summary>
    public ulong TotalAllocatedBytes { get; set; }

    /// <summary>Total resident bytes across all categories.</summary>
    public ulong TotalResidentBytes { get; set; }

    /// <summary>Allocated Memory Distribution rows (Native, Managed, Executables &amp; Mapped, Graphics, Untracked).</summary>
    public List<SummaryCategory> AllocatedMemoryDistribution { get; } = [];

    /// <summary>Managed Heap Utilization rows (Virtual Machine, Objects, Empty Heap Space).</summary>
    public List<SummaryCategory> ManagedHeapUtilization { get; } = [];
}

/// <summary>
/// Stable identifiers and flattening for the <c>summary_metrics</c> table, shared by the writers and validation.
/// </summary>
public static class SummaryMetricsTable
{
    /// <summary>Group name for the Allocated Memory Distribution rows.</summary>
    public const string GroupAllocatedMemoryDistribution = "AllocatedMemoryDistribution";

    /// <summary>Group name for the Managed Heap Utilization rows.</summary>
    public const string GroupManagedHeapUtilization = "ManagedHeapUtilization";

    /// <summary>Group name for the overall totals row.</summary>
    public const string GroupTotals = "Totals";

    /// <summary>Category name for the single totals row (committed = Total Allocated, resident = Total Resident).</summary>
    public const string CategoryTotal = "Total";

    /// <summary>Flattens summary metrics into one row per category for storage in <c>summary_metrics</c>.</summary>
    public static IEnumerable<(string Group, string Category, ulong Committed, ulong Resident, bool ResidentAvailable)> Enumerate(SummaryMetrics metrics)
    {
        yield return (GroupTotals, CategoryTotal, metrics.TotalAllocatedBytes, metrics.TotalResidentBytes, true);

        foreach (var row in metrics.AllocatedMemoryDistribution)
            yield return (GroupAllocatedMemoryDistribution, row.Name, row.CommittedBytes, row.ResidentBytes, row.ResidentAvailable);

        foreach (var row in metrics.ManagedHeapUtilization)
            yield return (GroupManagedHeapUtilization, row.Name, row.CommittedBytes, row.ResidentBytes, row.ResidentAvailable);
    }
}

/// <summary>
/// One summary breakdown row: a named category with committed and resident byte totals.
/// </summary>
public sealed class SummaryCategory
{
    /// <summary>Category label, matching the Memory Profiler summary row (e.g. "Native", "Graphics (Estimated)").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Committed (allocated) bytes for this category.</summary>
    public ulong CommittedBytes { get; set; }

    /// <summary>Resident bytes for this category (meaningful only when <see cref="ResidentAvailable"/> is true).</summary>
    public ulong ResidentBytes { get; set; }

    /// <summary>
    /// False for categories where resident size cannot be measured (Graphics, Untracked); those compare committed only.
    /// </summary>
    public bool ResidentAvailable { get; set; } = true;
}

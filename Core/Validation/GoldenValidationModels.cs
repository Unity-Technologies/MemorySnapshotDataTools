using System.Text.Json.Serialization;

namespace MemorySnapshotDataTools.Validation;

/// <summary>
/// Golden reference metrics JSON produced by the Unity <c>GoldenValueExtractor</c> editor script.
/// </summary>
public sealed class GoldenSnapshotFile
{
    /// <summary>Base name of the snapshot file (no extension).</summary>
    [JsonPropertyName("SnapshotName")]
    public string? SnapshotName { get; set; }

    /// <summary>Full path to the source .snap file.</summary>
    [JsonPropertyName("SnapshotPath")]
    public string? SnapshotPath { get; set; }

    /// <summary>Snapshot format version from Memory Profiler metadata.</summary>
    [JsonPropertyName("FormatVersion")]
    public int FormatVersion { get; set; }

    /// <summary>UTC timestamp when golden values were extracted.</summary>
    [JsonPropertyName("ExtractedAtUtc")]
    public string? ExtractedAtUtc { get; set; }

    /// <summary>Tracked AssetBundle and SerializedFile metrics.</summary>
    [JsonPropertyName("NativeTypeMetrics")]
    public GoldenNativeTypeMetric[]? NativeTypeMetrics { get; set; }

    /// <summary>Remapper / PersistentManager native root metrics.</summary>
    [JsonPropertyName("NativeRootMetrics")]
    public GoldenNativeRootMetric[]? NativeRootMetrics { get; set; }

    /// <summary>Total committed (allocated) bytes from the Summary page.</summary>
    [JsonPropertyName("TotalAllocatedBytes")]
    public long TotalAllocatedBytes { get; set; }

    /// <summary>Total resident bytes from the Summary page.</summary>
    [JsonPropertyName("TotalResidentBytes")]
    public long TotalResidentBytes { get; set; }

    /// <summary>Allocated Memory Distribution rows (Native, Managed, Executables &amp; Mapped, Graphics, Untracked).</summary>
    [JsonPropertyName("AllocatedMemoryDistribution")]
    public GoldenSummaryCategory[]? AllocatedMemoryDistribution { get; set; }

    /// <summary>Managed Heap Utilization rows (Virtual Machine, Objects, Empty Heap Space).</summary>
    [JsonPropertyName("ManagedHeapUtilization")]
    public GoldenSummaryCategory[]? ManagedHeapUtilization { get; set; }
}

/// <summary>
/// One MemoryProfiler Summary-page breakdown row from golden data.
/// </summary>
public sealed class GoldenSummaryCategory
{
    /// <summary>Category label (e.g. "Native", "Graphics (Estimated)", "Virtual Machine").</summary>
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    /// <summary>Committed (allocated) bytes.</summary>
    [JsonPropertyName("CommittedBytes")]
    public long CommittedBytes { get; set; }

    /// <summary>Resident bytes (meaningful only when <see cref="ResidentAvailable"/> is true).</summary>
    [JsonPropertyName("ResidentBytes")]
    public long ResidentBytes { get; set; }

    /// <summary>False for categories whose resident size cannot be measured (Graphics, Untracked).</summary>
    [JsonPropertyName("ResidentAvailable")]
    public bool ResidentAvailable { get; set; }
}

/// <summary>
/// Aggregated allocated and resident memory for a tracked Memory Profiler breakdown row.
/// </summary>
public sealed class GoldenNativeTypeMetric
{
    /// <summary>Metric label (e.g. AssetBundle or SerializedFile).</summary>
    [JsonPropertyName("NativeTypeName")]
    public string? NativeTypeName { get; set; }

    /// <summary>Object or root count.</summary>
    [JsonPropertyName("Count")]
    public int Count { get; set; }

    /// <summary>Committed (allocated) bytes.</summary>
    [JsonPropertyName("AllocatedBytes")]
    public long AllocatedBytes { get; set; }

    /// <summary>Resident bytes.</summary>
    [JsonPropertyName("ResidentBytes")]
    public long ResidentBytes { get; set; }
}

/// <summary>
/// Aggregated memory for a native root reference from Memory Profiler.
/// </summary>
public sealed class GoldenNativeRootMetric
{
    /// <summary>Root area name.</summary>
    [JsonPropertyName("AreaName")]
    public string? AreaName { get; set; }

    /// <summary>Root object name.</summary>
    [JsonPropertyName("ObjectName")]
    public string? ObjectName { get; set; }

    /// <summary>Committed (allocated) bytes.</summary>
    [JsonPropertyName("AllocatedBytes")]
    public long AllocatedBytes { get; set; }

    /// <summary>Resident bytes.</summary>
    [JsonPropertyName("ResidentBytes")]
    public long ResidentBytes { get; set; }
}

/// <summary>
/// Result of comparing an exported database against a golden JSON file.
/// </summary>
public sealed class GoldenValidationResult
{
    /// <summary>Snapshot base name validated.</summary>
    [JsonPropertyName("SnapshotName")]
    public string SnapshotName { get; set; } = string.Empty;

    /// <summary>Path to the golden JSON file.</summary>
    [JsonPropertyName("GoldenPath")]
    public string GoldenPath { get; set; } = string.Empty;

    /// <summary>Path to the database that was queried.</summary>
    [JsonPropertyName("DatabasePath")]
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>UTC timestamp when validation ran.</summary>
    [JsonPropertyName("ValidatedAtUtc")]
    public string ValidatedAtUtc { get; set; } = string.Empty;

    /// <summary>True when all metrics matched.</summary>
    [JsonPropertyName("Passed")]
    public bool Passed { get; set; }

    /// <summary>Human-readable failure descriptions.</summary>
    [JsonPropertyName("Failures")]
    public string[] Failures { get; set; } = [];
}

/// <summary>
/// Metrics read from an exported <c>native_objects</c> / <c>native_roots</c> database.
/// </summary>
public sealed class ExportedMetrics
{
    /// <summary>Metrics keyed by native type / metric name.</summary>
    public Dictionary<string, GoldenNativeTypeMetric> TypeMetrics { get; init; } =
        new(StringComparer.Ordinal);

    /// <summary>Remapper root rows from the export.</summary>
    public List<GoldenNativeRootMetric> RemapperRoots { get; init; } = [];

    /// <summary>Summary-page rows from the export's <c>summary_metrics</c> table, keyed by "group/category".</summary>
    public Dictionary<string, GoldenSummaryCategory> SummaryCategories { get; init; } =
        new(StringComparer.Ordinal);
}

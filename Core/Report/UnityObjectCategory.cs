namespace MemorySnapshotDataTools.Report;

/// <summary>
/// Aggregate native (Unity) object memory for a single object type, mirroring the "Unity Objects"
/// breakdown of the Memory Profiler window grouped by type. Sizes are native allocated bytes
/// (resident is intentionally excluded: the per-object resident value is a shared per-root total,
/// so summing it across a type would double-count).
/// </summary>
public sealed class UnityObjectCategory
{
    /// <summary>Native type name (e.g. "Texture2D", "Mesh"), or "(unknown)" when unresolved.</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>Number of live objects of this type.</summary>
    public long Count { get; set; }

    /// <summary>Total native allocated bytes across objects of this type.</summary>
    public ulong AllocatedBytes { get; set; }
}

/// <summary>Builds the "top Unity object categories" breakdown by grouping native objects by type.</summary>
public static class UnityObjectCategories
{
    /// <summary>Default number of categories shown by the summary command.</summary>
    public const int DefaultTopCount = 12;

    private const string UnknownType = "(unknown)";

    /// <summary>
    /// Groups live native objects by type, summing allocated bytes and counting objects, sorted by
    /// allocated bytes descending (ties broken by type name). Destroyed objects are excluded.
    /// </summary>
    public static List<UnityObjectCategory> FromNativeObjects(IEnumerable<NativeObjectRow> nativeObjects)
    {
        var byType = new Dictionary<string, UnityObjectCategory>(StringComparer.Ordinal);
        foreach (var obj in nativeObjects)
        {
            if (obj.IsDestroyed)
                continue;

            var name = string.IsNullOrEmpty(obj.NativeTypeName) ? UnknownType : obj.NativeTypeName;
            if (!byType.TryGetValue(name, out var category))
            {
                category = new UnityObjectCategory { TypeName = name };
                byType[name] = category;
            }

            category.Count++;
            category.AllocatedBytes += obj.SizeBytes;
        }

        return Sort(byType.Values);
    }

    /// <summary>Sorts categories by allocated bytes descending, then by type name.</summary>
    public static List<UnityObjectCategory> Sort(IEnumerable<UnityObjectCategory> categories) =>
        categories
            .OrderByDescending(c => c.AllocatedBytes)
            .ThenBy(c => c.TypeName, StringComparer.Ordinal)
            .ToList();
}

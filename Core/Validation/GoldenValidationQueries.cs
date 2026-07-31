namespace MemorySnapshotDataTools.Validation;

/// <summary>
/// SQL used to read validation metrics from exported DuckDB or SQLite databases.
/// </summary>
internal static class GoldenValidationQueries
{
    public const string AssetBundleNativeTypeName = "AssetBundle";
    public const string SerializedFileMetricName = "SerializedFile";

    public const string SerializedFileAreaPredicate =
        "LOWER(COALESCE(area_name, '')) LIKE '%serializedfile%'";

    public const string AssetBundleSql = """
        SELECT COUNT(*) AS obj_count,
               COALESCE(SUM(size_bytes), 0) AS allocated_bytes,
               COALESCE(SUM(resident_size_bytes), 0) AS resident_bytes
        FROM native_objects
        WHERE native_type_name = 'AssetBundle'
          AND is_destroyed = false
        """;

    public const string SerializedFileSql = """
        SELECT COUNT(*) AS obj_count,
               COALESCE(SUM(accumulated_size_bytes), 0) AS allocated_bytes,
               COALESCE(SUM(resident_size_bytes), 0) AS resident_bytes
        FROM native_roots
        WHERE LOWER(COALESCE(area_name, '')) LIKE '%serializedfile%'
        """;

    public const string RemapperRootsDuckDbSql = """
        SELECT area_name, object_name,
               COALESCE(SUM(accumulated_size_bytes), 0) AS allocated_bytes,
               COALESCE(SUM(resident_size_bytes), 0) AS resident_bytes
        FROM native_roots
        WHERE object_name LIKE '%Remapper%'
           OR (COALESCE(area_name, '') || ':' || COALESCE(object_name, '')) LIKE '%PersistentManager%Remapper%'
        GROUP BY area_name, object_name
        """;

    public const string RemapperRootsSqliteSql = """
        SELECT area_name, object_name,
               COALESCE(SUM(accumulated_size_bytes), 0) AS allocated_bytes,
               COALESCE(SUM(resident_size_bytes), 0) AS resident_bytes
        FROM native_roots
        WHERE object_name LIKE '%Remapper%'
           OR (IFNULL(area_name, '') || ':' || IFNULL(object_name, '')) LIKE '%PersistentManager%Remapper%'
        GROUP BY area_name, object_name
        """;

    public const string SummaryMetricsSql = """
        SELECT metric_group, category, committed_bytes, resident_bytes, resident_available
        FROM summary_metrics
        """;

    /// <summary>
    /// Extended summary read including the schema v2.0 swapped columns. Only run this after a
    /// column-presence check (HasColumn) — older databases lack the columns.
    /// </summary>
    public const string SummaryMetricsWithSwappedSql = """
        SELECT metric_group, category, committed_bytes, resident_bytes, resident_available, swapped_bytes, swapped_available
        FROM summary_metrics
        """;
}

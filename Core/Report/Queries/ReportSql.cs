namespace MemorySnapshotDataTools.Report.Queries;

/// <summary>
/// SQL strings and helpers for report queries. Constants are used by <see cref="ReportBuilder"/>; dialect-specific methods (e.g. <see cref="SizeBucketDistribution"/>) take <see cref="ReportBackendDialect"/>.
/// </summary>
internal static class ReportSql
{
    /// <summary>Query for snapshot_info row (path, exported_at_utc, unity_version).</summary>
    public const string SnapshotInfo = "SELECT snapshot_path, exported_at_utc, unity_version FROM snapshot_info;";

    /// <summary>Query for the stored schema version. Only run when the schema_meta table exists (use HasColumn first).</summary>
    public const string SchemaMeta = "SELECT schema_version_major, schema_version_minor FROM schema_meta LIMIT 1;";

    public const string TableCounts = """
        SELECT 'native_objects'    AS table_name, COUNT(*) AS row_count FROM native_objects
        UNION ALL SELECT 'managed_objects', COUNT(*) FROM managed_objects
        UNION ALL SELECT 'connections', COUNT(*) FROM connections
        UNION ALL SELECT 'native_roots', COUNT(*) FROM native_roots
        UNION ALL SELECT 'memory_regions', COUNT(*) FROM memory_regions
        UNION ALL SELECT 'native_allocations', COUNT(*) FROM native_allocations
        ORDER BY 1;
        """;

    public const string NativeOverview = """
        SELECT
            COUNT(*) AS total_objects,
            ROUND(SUM(size_bytes) / 1024.0 / 1024, 2) AS total_native_mb,
            ROUND(SUM(size_bytes) / 1024.0 / 1024 / 1024, 3) AS total_native_gb,
            ROUND(AVG(size_bytes) / 1024.0, 2) AS avg_size_kb,
            ROUND(MAX(size_bytes) / 1024.0 / 1024, 2) AS max_single_object_mb,
            COUNT(DISTINCT native_type_name) AS distinct_types
        FROM native_objects;
        """;

    public const string NativeTypes = """
        SELECT
            COALESCE(native_type_name, '(unknown)') AS type_name,
            COUNT(*) AS obj_count,
            ROUND(SUM(size_bytes) / 1024.0 / 1024, 2) AS total_mb,
            ROUND(100.0 * SUM(size_bytes) / NULLIF(SUM(SUM(size_bytes)) OVER (), 0), 2) AS pct_of_total
        FROM native_objects
        GROUP BY native_type_name
        ORDER BY total_mb DESC
        LIMIT 40;
        """;

    /// <summary>Returns SQL for native object size distribution by log4 bucket. DuckDB uses LOG(4,x); SQLite uses log(x)/log(4).</summary>
    /// <param name="dialect">Backend dialect for LOG function.</param>
    /// <returns>SQL string for the size bucket query.</returns>
    public static string SizeBucketDistribution(ReportBackendDialect dialect) => dialect switch
    {
        ReportBackendDialect.DuckDb => """
            SELECT
                CAST(FLOOR(LOG(4, NULLIF(size_bytes, 0))) AS INTEGER) AS log4_bucket,
                ROUND(POWER(4.0, CAST(FLOOR(LOG(4, NULLIF(size_bytes, 0))) AS INTEGER)) / 1024.0 / 1024, 3) AS bucket_floor_mb,
                COUNT(*) AS obj_count,
                ROUND(SUM(size_bytes) / 1024.0 / 1024, 2) AS total_mb
            FROM native_objects
            WHERE size_bytes > 0
            GROUP BY log4_bucket
            ORDER BY log4_bucket DESC;
            """,
        ReportBackendDialect.Sqlite => """
            SELECT
                CAST(FLOOR(CAST(log(NULLIF(size_bytes, 0)) / log(4) AS REAL)) AS INTEGER) AS log4_bucket,
                ROUND(POWER(4.0, CAST(FLOOR(CAST(log(NULLIF(size_bytes, 0)) / log(4) AS REAL)) AS INTEGER)) / 1024.0 / 1024, 3) AS bucket_floor_mb,
                COUNT(*) AS obj_count,
                ROUND(SUM(size_bytes) / 1024.0 / 1024, 2) AS total_mb
            FROM native_objects
            WHERE size_bytes > 0
            GROUP BY log4_bucket
            ORDER BY log4_bucket DESC;
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

    public const string TopNativeObjects = """
        SELECT
            native_object_index,
            COALESCE(name, '(unnamed)') AS name,
            COALESCE(native_type_name, '(unknown)') AS type_name,
            ROUND(size_bytes / 1024.0 / 1024, 3) AS size_mb
        FROM native_objects
        ORDER BY size_bytes DESC
        LIMIT 50;
        """;

    public const string NativeTypesTop5Pct = """
        SELECT ROUND(SUM(pct), 1) AS top5_pct
        FROM (
            SELECT ROUND(100.0 * SUM(size_bytes) / NULLIF(SUM(SUM(size_bytes)) OVER (), 0), 2) AS pct
            FROM native_objects
            GROUP BY native_type_name
            ORDER BY SUM(size_bytes) DESC
            LIMIT 5
        ) t;
        """;

    public const string DuplicateAssets = """
        SELECT
            COALESCE(name, '(unnamed)') AS name,
            COALESCE(native_type_name, '(unknown)') AS type_name,
            COUNT(*) AS duplicate_count,
            ROUND(MIN(size_bytes) / 1024.0 / 1024, 3) AS min_size_mb,
            ROUND(MAX(size_bytes) / 1024.0 / 1024, 3) AS max_size_mb,
            ROUND(SUM(size_bytes) / 1024.0 / 1024, 3) AS total_mb,
            ROUND((COUNT(*) - 1) * AVG(size_bytes) / 1024.0 / 1024, 3) AS wasted_mb
        FROM native_objects
        WHERE name IS NOT NULL
        GROUP BY name, native_type_name
        HAVING COUNT(*) > 1
        ORDER BY wasted_mb DESC
        LIMIT 50;
        """;

    public const string DuplicateSummary = """
        SELECT
            COUNT(*) AS duplicate_groups,
            SUM(dup_count) - COUNT(*) AS extra_instances,
            ROUND(SUM(wasted_bytes) / 1024.0 / 1024, 2) AS total_wasted_mb,
            ROUND(100.0 * SUM(wasted_bytes) / NULLIF((SELECT SUM(size_bytes) FROM native_objects), 0), 1) AS pct_of_native_total
        FROM (
            SELECT COUNT(*) AS dup_count, (COUNT(*) - 1) * AVG(size_bytes) AS wasted_bytes
            FROM native_objects
            WHERE name IS NOT NULL
            GROUP BY name, native_type_name
            HAVING COUNT(*) > 1
        ) t;
        """;

    public const string ManagedOverview = """
        SELECT
            COUNT(*) AS total_objects,
            ROUND(SUM(size_bytes) / 1024.0 / 1024, 2) AS total_managed_mb,
            ROUND(AVG(size_bytes), 0) AS avg_size_bytes,
            COUNT(DISTINCT managed_type_name) AS distinct_types,
            COUNT(native_object_index) AS objects_with_native_ref
        FROM managed_objects;
        """;

    public const string ManagedTypes = """
        SELECT
            COALESCE(managed_type_name, '(unknown)') AS type_name,
            COUNT(*) AS obj_count,
            ROUND(SUM(size_bytes) / 1024.0 / 1024, 2) AS total_mb,
            ROUND(100.0 * SUM(size_bytes) / NULLIF(SUM(SUM(size_bytes)) OVER (), 0), 2) AS pct_of_total
        FROM managed_objects
        GROUP BY managed_type_name
        ORDER BY total_mb DESC
        LIMIT 40;
        """;

    public const string NativeRootsByArea = """
        SELECT
            COALESCE(area_name, '(unknown)') AS area_name,
            COUNT(*) AS root_count,
            ROUND(SUM(accumulated_size_bytes) / 1024.0 / 1024, 2) AS total_accumulated_mb
        FROM native_roots
        GROUP BY area_name
        ORDER BY total_accumulated_mb DESC;
        """;

    public const string NativeRootsTop = """
        SELECT
            root_id,
            COALESCE(area_name, '(unknown)') AS area_name,
            COALESCE(object_name, '(unnamed)') AS object_name,
            ROUND(accumulated_size_bytes / 1024.0 / 1024, 3) AS accumulated_mb
        FROM native_roots
        ORDER BY accumulated_size_bytes DESC
        LIMIT 30;
        """;

    public const string MemoryRegions = """
        SELECT
            r.region_index,
            COALESCE(r.name, '(unnamed)') AS region_name,
            COALESCE(p.name, '—') AS parent_name,
            ROUND(r.address_size / 1024.0 / 1024, 2) AS size_mb,
            r.num_allocations
        FROM memory_regions r
        LEFT JOIN memory_regions p ON p.region_index = r.parent_region_index
        ORDER BY r.address_size DESC
        LIMIT 40;
        """;

    public const string AllocationEfficiency = """
        SELECT
            COALESCE(r.name, '(unnamed)') AS region_name,
            r.num_allocations,
            ROUND(r.address_size / 1024.0 / 1024, 2) AS region_size_mb,
            ROUND(SUM(a.size_bytes) / 1024.0 / 1024, 2) AS payload_mb,
            ROUND(SUM(a.overhead_size_bytes) / 1024.0 / 1024, 2) AS overhead_mb,
            ROUND(SUM(a.padding_size_bytes) / 1024.0 / 1024, 2) AS padding_mb,
            ROUND(100.0 * SUM(a.size_bytes) / NULLIF(r.address_size, 0), 1) AS utilization_pct
        FROM memory_regions r
        LEFT JOIN native_allocations a ON a.memory_region_index = r.region_index
        GROUP BY r.region_index, r.name, r.address_size, r.num_allocations
        HAVING SUM(a.size_bytes) IS NOT NULL
        ORDER BY payload_mb DESC NULLS LAST
        LIMIT 30;
        """;

    public const string ConnectionTypes = """
        SELECT connection_type, COUNT(*) AS edge_count
        FROM connections
        GROUP BY connection_type
        ORDER BY edge_count DESC;
        """;

    public const string MostReferenced = """
        SELECT
            n.native_object_index,
            COALESCE(n.name, '(unnamed)') AS name,
            COALESCE(n.native_type_name, '(unknown)') AS type_name,
            ROUND(n.size_bytes / 1024.0 / 1024, 2) AS size_mb,
            COUNT(c.from_index) AS inbound_refs
        FROM connections c
        JOIN native_objects n ON n.native_object_index = c.to_index
        WHERE c.to_kind = 'native_object'
        GROUP BY n.native_object_index, n.name, n.native_type_name, n.size_bytes
        ORDER BY inbound_refs DESC
        LIMIT 20;
        """;

    public const string MostReferencedExclMonoScript = """
        SELECT
            n.native_object_index,
            COALESCE(n.name, '(unnamed)') AS name,
            COALESCE(n.native_type_name, '(unknown)') AS type_name,
            ROUND(n.size_bytes / 1024.0 / 1024, 2) AS size_mb,
            COUNT(c.from_index) AS inbound_refs
        FROM connections c
        JOIN native_objects n ON n.native_object_index = c.to_index
        WHERE c.to_kind = 'native_object' AND n.native_type_name != 'MonoScript'
        GROUP BY n.native_object_index, n.name, n.native_type_name, n.size_bytes
        ORDER BY inbound_refs DESC
        LIMIT 20;
        """;

    public const string MostOutbound = """
        SELECT
            n.native_object_index,
            COALESCE(n.name, '(unnamed)') AS name,
            COALESCE(n.native_type_name, '(unknown)') AS type_name,
            ROUND(n.size_bytes / 1024.0 / 1024, 2) AS size_mb,
            COUNT(c.to_index) AS outbound_refs
        FROM connections c
        JOIN native_objects n ON n.native_object_index = c.from_index
        WHERE c.from_kind = 'native_object'
        GROUP BY n.native_object_index, n.name, n.native_type_name, n.size_bytes
        ORDER BY outbound_refs DESC
        LIMIT 20;
        """;

    public const string Top50Summary = """
        SELECT
            COUNT(*) AS obj_count,
            ROUND(SUM(size_bytes) / 1024.0 / 1024, 2) AS total_mb,
            ROUND(100.0 * SUM(size_bytes) / NULLIF((SELECT SUM(size_bytes) FROM native_objects), 0), 1) AS pct_of_native_total
        FROM (SELECT size_bytes FROM native_objects ORDER BY size_bytes DESC LIMIT 50) t;
        """;

    // ---------------------------------------------------------------------------
    // Leaked Shell analysis
    // Pattern A: native object still in memory but is_destroyed=true
    // Pattern B: native object freed; managed C# wrapper is orphaned (native_object_index IS NULL)
    // ---------------------------------------------------------------------------

    public const string LeakedBByType = """
        SELECT
            COALESCE(m.managed_type_name, '(unknown)') AS managed_type,
            COUNT(1) AS leaked_count
        FROM managed_objects m
        WHERE m.native_object_index IS NULL
          AND EXISTS (
              SELECT 1 FROM managed_objects m2
              WHERE m2.managed_type_name = m.managed_type_name
                AND m2.native_object_index IS NOT NULL
          )
        GROUP BY 1
        ORDER BY leaked_count DESC;
        """;

    public const string LeakedBStats = """
        SELECT COUNT(1) AS total_orphaned
        FROM managed_objects m
        WHERE m.native_object_index IS NULL
          AND EXISTS (
              SELECT 1 FROM managed_objects m2
              WHERE m2.managed_type_name = m.managed_type_name
                AND m2.native_object_index IS NOT NULL
          );
        """;

    public const string LeakedCombined = """
        SELECT
            pattern,
            COALESCE(native_type_name, 'unknown (freed)') AS native_type_name,
            managed_type_name,
            leaked_count,
            ROUND(native_mb_retained, 2) AS native_mb_retained
        FROM (
            SELECT
                'A: Destroyed (native still in memory)' AS pattern,
                n.native_type_name,
                m.managed_type_name,
                COUNT(1) AS leaked_count,
                SUM(n.size_bytes) / 1024.0 / 1024 AS native_mb_retained
            FROM managed_objects m
            JOIN native_objects n ON m.native_object_index = n.native_object_index
            WHERE n.is_destroyed = true
            GROUP BY 2, 3

            UNION ALL

            SELECT
                'B: Orphaned (native freed)',
                'unknown (freed)',
                m.managed_type_name,
                COUNT(1),
                0.0
            FROM managed_objects m
            WHERE m.native_object_index IS NULL
              AND EXISTS (
                  SELECT 1 FROM managed_objects m2
                  WHERE m2.managed_type_name = m.managed_type_name
                    AND m2.native_object_index IS NOT NULL
              )
            GROUP BY 3
        ) combined
        ORDER BY leaked_count DESC;
        """;

    public const string LeakedAStats = """
        SELECT
            COUNT(*) AS total_leaked_count,
            ROUND(SUM(n.size_bytes) / 1024.0 / 1024, 2) AS native_mb_retained,
            ROUND(
                100.0 * SUM(n.size_bytes) / NULLIF((SELECT SUM(size_bytes) FROM native_objects), 0),
                1
            ) AS pct_of_native_total
        FROM managed_objects m
        JOIN native_objects n ON m.native_object_index = n.native_object_index
        WHERE n.is_destroyed = true;
        """;

    public const string LeakedAByType = """
        SELECT
            COALESCE(n.native_type_name, '(unknown)') AS native_type,
            COALESCE(m.managed_type_name, '(unknown)') AS managed_type,
            COUNT(1) AS leaked_count,
            ROUND(SUM(n.size_bytes) / 1024.0 / 1024, 2) AS native_mb_retained
        FROM managed_objects m
        JOIN native_objects n ON m.native_object_index = n.native_object_index
        WHERE n.is_destroyed = true
        GROUP BY 1, 2
        ORDER BY native_mb_retained DESC;
        """;

    public const string LeakedATopObjects = """
        SELECT
            n.native_object_index,
            COALESCE(n.name, '(unnamed)') AS name,
            COALESCE(n.native_type_name, '(unknown)') AS native_type,
            COALESCE(m.managed_type_name, '(unknown)') AS managed_type,
            ROUND(n.size_bytes / 1024.0 / 1024, 2) AS own_size_mb
        FROM managed_objects m
        JOIN native_objects n ON m.native_object_index = n.native_object_index
        WHERE n.is_destroyed = true
        ORDER BY n.size_bytes DESC
        LIMIT 20;
        """;

    public const string AllDestroyedNatives = """
        SELECT
            COALESCE(native_type_name, '(unknown)') AS native_type,
            COUNT(1) AS destroyed_count,
            ROUND(SUM(size_bytes) / 1024.0 / 1024, 2) AS native_mb_retained
        FROM native_objects
        WHERE is_destroyed = true
        GROUP BY 1
        ORDER BY native_mb_retained DESC;
        """;

    public const string AllDestroyedStats = """
        SELECT
            COUNT(*) AS total_destroyed,
            ROUND(SUM(size_bytes) / 1024.0 / 1024, 2) AS native_mb_retained,
            ROUND(
                100.0 * SUM(size_bytes) / NULLIF((SELECT SUM(size_bytes) FROM native_objects), 0),
                1
            ) AS pct_of_native_total
        FROM native_objects
        WHERE is_destroyed = true;
        """;

    /// <summary>
    /// Returns SQL for downstream_mb and exclusive_mb for a single native root.
    /// rootIdx must be from our own query result (safe to interpolate).
    /// </summary>
    public static string DownstreamStats(long rootIdx)
    {
        return $"""
            WITH RECURSIVE
            reachable(node_index) AS (
                SELECT c.to_index
                FROM connections c
                WHERE c.from_index = {rootIdx}
                  AND c.from_kind = 'native_object'
                  AND c.to_kind   = 'native_object'
                  AND c.connection_type = 'native_connection'
                UNION
                SELECT c.to_index
                FROM reachable r
                JOIN connections c ON c.from_index = r.node_index
                WHERE c.from_kind = 'native_object'
                  AND c.to_kind   = 'native_object'
                  AND c.connection_type = 'native_connection'
            ),
            reachable_set AS (SELECT DISTINCT node_index FROM reachable),
            externally_referenced AS (
                SELECT DISTINCT c.to_index AS node_index
                FROM connections c
                JOIN  reachable_set rs_to   ON rs_to.node_index   = c.to_index
                LEFT JOIN reachable_set rs_from ON rs_from.node_index = c.from_index
                WHERE c.from_kind = 'native_object'
                  AND c.to_kind   = 'native_object'
                  AND c.connection_type = 'native_connection'
                  AND c.from_index  != {rootIdx}
                  AND rs_from.node_index IS NULL
            ),
            exclusive_set AS (
                SELECT rs.node_index
                FROM reachable_set rs
                LEFT JOIN externally_referenced ext ON ext.node_index = rs.node_index
                WHERE ext.node_index IS NULL
            )
            SELECT
                COALESCE(
                    (SELECT ROUND(SUM(n.size_bytes) / 1024.0 / 1024, 2)
                     FROM reachable_set rs
                     JOIN native_objects n ON n.native_object_index = rs.node_index),
                    0.0) AS downstream_mb,
                COALESCE(
                    (SELECT ROUND(SUM(n.size_bytes) / 1024.0 / 1024, 2)
                     FROM exclusive_set es
                     JOIN native_objects n ON n.native_object_index = es.node_index),
                    0.0) AS exclusive_mb;
            """;
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using DuckDB.NET.Data;

namespace MemorySnapshotDataTools.ExportDestination;

/// <summary>
/// DuckDB implementation of <see cref="IExportDestinationWriter"/>. Writes snapshot tables to a .duckdb file using DuckDB appenders,
/// then builds indexes. Supports validation via row counts and optional referential checks.
/// </summary>
internal sealed class DuckDbExportDestination : IExportDestinationWriter
{
    /// <inheritdoc/>
    public string DestinationName => "duckdb";

    #region ConsumeAndWrite

    /// <inheritdoc/>
    public WriteStats ConsumeAndWrite(
        string dbPath,
        SnapshotInfo snapshotInfo,
        BlockingCollection<WriteBatch> queue,
        PipelineState state,
        CancellationToken token)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Remove any existing DuckDB files so we start fresh.
        // DuckDB creates a WAL alongside the main file; both must be deleted to avoid replay.
        foreach (var suffix in new[] { "", ".wal" })
        {
            var f = dbPath + suffix;
            if (File.Exists(f))
                File.Delete(f);
        }

        using var connection = new DuckDBConnection($"Data Source={dbPath}");
        connection.Open();

        var stats = new WriteStats();

        // Create schema
        Exec(connection, SchemaTablesScript);

        // Insert snapshot_info using positional parameters (DuckDB uses ? placeholders)
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO snapshot_info(
                    snapshot_path, exported_at_utc, unity_version,
                    snap_format_version, session_guid, product_name, platform, record_date_utc)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?);
                """;
            cmd.Parameters.Add(new DuckDBParameter { Value = snapshotInfo.SnapshotPath });
            cmd.Parameters.Add(new DuckDBParameter { Value = snapshotInfo.ExportedAtUtc });
            cmd.Parameters.Add(new DuckDBParameter { Value = snapshotInfo.UnityVersion ?? (object)DBNull.Value });
            cmd.Parameters.Add(new DuckDBParameter { Value = snapshotInfo.SnapFormatVersion == 0 ? (object)DBNull.Value : snapshotInfo.SnapFormatVersion });
            cmd.Parameters.Add(new DuckDBParameter
            {
                Value = snapshotInfo.SessionGuid == 0 ? (object)DBNull.Value : unchecked((long)snapshotInfo.SessionGuid),
            });
            cmd.Parameters.Add(new DuckDBParameter { Value = string.IsNullOrEmpty(snapshotInfo.ProductName) ? (object)DBNull.Value : snapshotInfo.ProductName });
            cmd.Parameters.Add(new DuckDBParameter { Value = string.IsNullOrEmpty(snapshotInfo.Platform) ? (object)DBNull.Value : snapshotInfo.Platform });
            cmd.Parameters.Add(new DuckDBParameter { Value = string.IsNullOrEmpty(snapshotInfo.RecordDateUtc) ? (object)DBNull.Value : snapshotInfo.RecordDateUtc });
            cmd.ExecuteNonQuery();
        }
        state.AddWritten(1);

        var insertSw = Stopwatch.StartNew();

        // Appenders are scoped so disposal (= flush+commit) is timed separately.
        using (var nativeAppender = connection.CreateAppender("native_objects"))
        using (var managedAppender = connection.CreateAppender("managed_objects"))
        using (var connectionAppender = connection.CreateAppender("connections"))
        using (var rootAppender = connection.CreateAppender("native_roots"))
        using (var regionAppender = connection.CreateAppender("memory_regions"))
        using (var allocationAppender = connection.CreateAppender("native_allocations"))
        using (var systemRegionAppender = connection.CreateAppender("system_memory_regions"))
        {
            foreach (var batch in queue.GetConsumingEnumerable(token))
            {
                token.ThrowIfCancellationRequested();
                state.DecrementQueuedBatches();
                switch (batch.Kind)
                {
                    case WriteBatchKind.NativeObjects:
                        var nativeSw = Stopwatch.StartNew();
                        foreach (var row in batch.NativeObjects)
                        {
                            // INTEGER columns get int, BIGINT columns get long (type must match exactly)
                            var nativeRow = nativeAppender.CreateRow()
                                .AppendValue(row.NativeObjectIndex)
                                .AppendValue(row.InstanceId ?? string.Empty)
                                .AppendValue(row.Name ?? string.Empty)
                                .AppendValue(unchecked((long)row.SizeBytes))
                                .AppendValue(unchecked((long)row.NativeObjectAddress))
                                .AppendValue(row.RootReferenceId)
                                .AppendValue(row.TypeIndex)
                                .AppendValue(row.NativeTypeName ?? string.Empty)
                                .AppendValue(row.IsDestroyed);
                            if (row.ResidentSizeBytes.HasValue)
                                nativeRow.AppendValue(unchecked((long)row.ResidentSizeBytes.Value));
                            else
                                nativeRow.AppendNullValue();
                            nativeRow.EndRow();
                        }
                        nativeSw.Stop();
                        stats.NativeObjectRows += batch.NativeObjects.Length;
                        stats.NativeObjectInsertMs += nativeSw.ElapsedMilliseconds;
                        state.AddWritten(batch.NativeObjects.Length);
                        break;

                    case WriteBatchKind.ManagedObjects:
                        var managedSw = Stopwatch.StartNew();
                        foreach (var row in batch.ManagedObjects)
                        {
                            var r = managedAppender.CreateRow()
                                .AppendValue(row.ManagedObjectIndex)          // int  → INTEGER
                                .AppendValue(unchecked((long)row.Address))    // ulong → BIGINT
                                .AppendValue(row.SizeBytes)                   // long → BIGINT
                                .AppendValue(row.TypeIndex)                   // int  → INTEGER
                                .AppendValue(row.ManagedTypeName ?? string.Empty); // VARCHAR
                            if (row.NativeObjectIndex >= 0)
                                r.AppendValue(row.NativeObjectIndex);         // long → BIGINT
                            else
                                r.AppendNullValue();
                            r.EndRow();
                        }
                        managedSw.Stop();
                        stats.ManagedObjectRows += batch.ManagedObjects.Length;
                        stats.ManagedObjectInsertMs += managedSw.ElapsedMilliseconds;
                        state.AddWritten(batch.ManagedObjects.Length);
                        break;

                    case WriteBatchKind.Connections:
                        var connSw = Stopwatch.StartNew();
                        foreach (var row in batch.Connections)
                        {
                            connectionAppender.CreateRow()
                                .AppendValue(row.FromKind ?? string.Empty)
                                .AppendValue(row.FromIndex)
                                .AppendValue(row.ToKind ?? string.Empty)
                                .AppendValue(row.ToIndex)
                                .AppendValue(row.ConnectionType ?? string.Empty)
                                .EndRow();
                        }
                        connSw.Stop();
                        stats.ConnectionRows += batch.Connections.Length;
                        stats.ConnectionInsertMs += connSw.ElapsedMilliseconds;
                        state.AddWritten(batch.Connections.Length);
                        break;

                    case WriteBatchKind.NativeRoots:
                        var rootSw = Stopwatch.StartNew();
                        foreach (var row in batch.NativeRoots)
                        {
                            var rootRow = rootAppender.CreateRow()
                                .AppendValue(row.RootIndex)
                                .AppendValue(row.RootId)
                                .AppendValue(row.AreaName ?? string.Empty)
                                .AppendValue(row.ObjectName ?? string.Empty)
                                .AppendValue(unchecked((long)row.AccumulatedSizeBytes));
                            if (row.ResidentSizeBytes.HasValue)
                                rootRow.AppendValue(unchecked((long)row.ResidentSizeBytes.Value));
                            else
                                rootRow.AppendNullValue();
                            rootRow.EndRow();
                        }
                        rootSw.Stop();
                        stats.NativeRootRows += batch.NativeRoots.Length;
                        stats.NativeRootInsertMs += rootSw.ElapsedMilliseconds;
                        state.AddWritten(batch.NativeRoots.Length);
                        break;

                    case WriteBatchKind.MemoryRegions:
                        var regionSw = Stopwatch.StartNew();
                        foreach (var row in batch.MemoryRegions)
                        {
                            var r = regionAppender.CreateRow()
                                .AppendValue(row.RegionIndex)                       // int  → INTEGER
                                .AppendValue(unchecked((long)row.AddressBase))      // ulong → BIGINT
                                .AppendValue(unchecked((long)row.AddressSize))      // ulong → BIGINT
                                .AppendValue(row.Name ?? string.Empty);             // VARCHAR
                            if (row.ParentRegionIndex >= 0)
                                r.AppendValue(row.ParentRegionIndex);               // int  → INTEGER
                            else
                                r.AppendNullValue();
                            if (row.FirstAllocationIndex >= 0)
                                r.AppendValue(row.FirstAllocationIndex);            // int  → INTEGER
                            else
                                r.AppendNullValue();
                            r.AppendValue(row.NumAllocations).EndRow();             // int  → INTEGER
                        }
                        regionSw.Stop();
                        stats.MemoryRegionRows += batch.MemoryRegions.Length;
                        stats.MemoryRegionInsertMs += regionSw.ElapsedMilliseconds;
                        state.AddWritten(batch.MemoryRegions.Length);
                        break;

                    case WriteBatchKind.NativeAllocations:
                        var allocSw = Stopwatch.StartNew();
                        foreach (var row in batch.NativeAllocations)
                        {
                            var r = allocationAppender.CreateRow()
                                .AppendValue(row.AllocationIndex)                      // int  → INTEGER
                                .AppendValue(unchecked((long)row.Address))             // ulong → BIGINT
                                .AppendValue(unchecked((long)row.SizeBytes))           // ulong → BIGINT
                                .AppendValue(unchecked((long)row.OverheadSizeBytes))   // ulong → BIGINT
                                .AppendValue(unchecked((long)row.PaddingSizeBytes));   // ulong → BIGINT
                            if (row.MemoryRegionIndex >= 0)
                                r.AppendValue(row.MemoryRegionIndex);
                            else
                                r.AppendNullValue();
                            if (row.RootReferenceId >= 0)
                                r.AppendValue(row.RootReferenceId);
                            else
                                r.AppendNullValue();
                            r.EndRow();
                        }
                        allocSw.Stop();
                        stats.NativeAllocationRows += batch.NativeAllocations.Length;
                        stats.NativeAllocationInsertMs += allocSw.ElapsedMilliseconds;
                        state.AddWritten(batch.NativeAllocations.Length);
                        break;

                    case WriteBatchKind.SystemMemoryRegions:
                        var sysSw = Stopwatch.StartNew();
                        foreach (var row in batch.SystemMemoryRegions)
                        {
                            systemRegionAppender.CreateRow()
                                .AppendValue(row.RegionIndex)
                                .AppendValue(unchecked((long)row.Address))
                                .AppendValue(unchecked((long)row.SizeBytes))
                                .AppendValue(unchecked((long)row.ResidentBytes))
                                .AppendValue(row.Type)
                                .AppendValue(row.Name ?? string.Empty)
                                .EndRow();
                        }
                        sysSw.Stop();
                        stats.SystemMemoryRegionRows += batch.SystemMemoryRegions.Length;
                        stats.SystemMemoryRegionInsertMs += sysSw.ElapsedMilliseconds;
                        state.AddWritten(batch.SystemMemoryRegions.Length);
                        break;
                }
            }
        } // appenders disposed (flushed + committed) here

        insertSw.Stop();
        stats.TotalInsertMs = insertSw.ElapsedMilliseconds;
        // CommitMs is included in TotalInsertMs since disposal happens inside the timed scope.
        stats.CommitMs = 0;

        var indexSw = Stopwatch.StartNew();
        Exec(connection, CreateIndexesScript);
        indexSw.Stop();
        stats.IndexBuildMs = indexSw.ElapsedMilliseconds;

        return stats;
    }

    #endregion

    #region SummaryMetrics

    /// <inheritdoc/>
    public void WriteSummaryMetrics(string dbPath, SummaryMetrics metrics)
    {
        using var connection = new DuckDBConnection($"Data Source={dbPath}");
        connection.Open();

        using var appender = connection.CreateAppender("summary_metrics");
        foreach (var (group, category, committed, resident, residentAvailable) in SummaryMetricsTable.Enumerate(metrics))
        {
            appender.CreateRow()
                .AppendValue(group)
                .AppendValue(category)
                .AppendValue(unchecked((long)committed))
                .AppendValue(unchecked((long)resident))
                .AppendValue(residentAvailable ? 1 : 0)
                .EndRow();
        }
    }

    #endregion

    #region Validation

    /// <inheritdoc/>
    public void Validate(string dbPath, RawSnapshotData rawData, ValidationMode mode)
    {
        if (mode == ValidationMode.None)
            return;

        using var connection = new DuckDBConnection($"Data Source={dbPath}");
        connection.Open();

        var nativeCount = QueryCount(connection, "SELECT COUNT(*) FROM native_objects;");
        var managedCount = QueryCount(connection, "SELECT COUNT(*) FROM managed_objects;");
        var connectionCount = QueryCount(connection, "SELECT COUNT(*) FROM connections;");
        var rootCount = QueryCount(connection, "SELECT COUNT(*) FROM native_roots;");
        var regionCount = QueryCount(connection, "SELECT COUNT(*) FROM memory_regions;");
        var allocationCount = QueryCount(connection, "SELECT COUNT(*) FROM native_allocations;");
        var systemRegionCount = QueryCount(connection, "SELECT COUNT(*) FROM system_memory_regions;");

        if (nativeCount != rawData.NativeObjects.Count ||
            managedCount != rawData.ManagedObjects.Count ||
            connectionCount != rawData.Connections.Count ||
            rootCount != rawData.NativeRoots.Count ||
            regionCount != rawData.MemoryRegions.Count ||
            allocationCount != rawData.NativeAllocations.Count ||
            systemRegionCount != rawData.SystemMemoryRegions.Count)
        {
            throw new InvalidOperationException(
                $"DuckDB validation count mismatch. " +
                $"expected=(native={rawData.NativeObjects.Count}, managed={rawData.ManagedObjects.Count}, " +
                $"connections={rawData.Connections.Count}, roots={rawData.NativeRoots.Count}, " +
                $"regions={rawData.MemoryRegions.Count}, allocations={rawData.NativeAllocations.Count}, " +
                $"system_regions={rawData.SystemMemoryRegions.Count}) " +
                $"actual=(native={nativeCount}, managed={managedCount}, connections={connectionCount}, " +
                $"roots={rootCount}, regions={regionCount}, allocations={allocationCount}, " +
                $"system_regions={systemRegionCount})");
        }

        if (mode == ValidationMode.Full)
        {
            var duplicateNativeKeys = QueryCount(connection, "SELECT COUNT(*) FROM (SELECT native_object_index, COUNT(*) c FROM native_objects GROUP BY native_object_index HAVING c > 1);");
            var duplicateManagedKeys = QueryCount(connection, "SELECT COUNT(*) FROM (SELECT managed_object_index, COUNT(*) c FROM managed_objects GROUP BY managed_object_index HAVING c > 1);");
            var duplicateRegionKeys = QueryCount(connection, "SELECT COUNT(*) FROM (SELECT region_index, COUNT(*) c FROM memory_regions GROUP BY region_index HAVING c > 1);");
            var duplicateAllocationKeys = QueryCount(connection, "SELECT COUNT(*) FROM (SELECT allocation_index, COUNT(*) c FROM native_allocations GROUP BY allocation_index HAVING c > 1);");
            if (duplicateNativeKeys > 0 || duplicateManagedKeys > 0 || duplicateRegionKeys > 0 || duplicateAllocationKeys > 0)
                throw new InvalidOperationException("DuckDB validation failed: duplicate primary key rows found.");

            var orphanFromManaged = QueryCount(connection, """
                SELECT COUNT(*) FROM connections c
                WHERE c.from_kind = 'managed_object'
                  AND NOT EXISTS (
                    SELECT 1 FROM managed_objects m WHERE m.managed_object_index = c.from_index
                  );
                """);
            var orphanFromNative = QueryCount(connection, """
                SELECT COUNT(*) FROM connections c
                WHERE c.from_kind = 'native_object'
                  AND NOT EXISTS (
                    SELECT 1 FROM native_objects n WHERE n.native_object_index = c.from_index
                  );
                """);
            var orphanToManaged = QueryCount(connection, """
                SELECT COUNT(*) FROM connections c
                WHERE c.to_kind = 'managed_object'
                  AND NOT EXISTS (
                    SELECT 1 FROM managed_objects m WHERE m.managed_object_index = c.to_index
                  );
                """);
            var orphanToNative = QueryCount(connection, """
                SELECT COUNT(*) FROM connections c
                WHERE c.to_kind = 'native_object'
                  AND NOT EXISTS (
                    SELECT 1 FROM native_objects n WHERE n.native_object_index = c.to_index
                  );
                """);
            var unknownKinds = QueryCount(connection, """
                SELECT COUNT(*) FROM connections
                WHERE from_kind NOT IN ('managed_object','native_object')
                   OR to_kind NOT IN ('managed_object','native_object');
                """);
            var orphanAllocationRegionRefs = QueryCount(connection, """
                SELECT COUNT(*) FROM native_allocations a
                WHERE a.memory_region_index IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM memory_regions r WHERE r.region_index = a.memory_region_index
                  );
                """);
            var orphanRegionFirstAllocationRefs = QueryCount(connection, """
                SELECT COUNT(*) FROM memory_regions r
                WHERE r.first_allocation_index IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM native_allocations a WHERE a.allocation_index = r.first_allocation_index
                  );
                """);

            if (orphanFromManaged > 0 || orphanFromNative > 0 || orphanToManaged > 0 || orphanToNative > 0 || unknownKinds > 0 ||
                orphanAllocationRegionRefs > 0 || orphanRegionFirstAllocationRefs > 0)
            {
                throw new InvalidOperationException(
                    $"DuckDB validation failed: invalid graph or memory-map references. " +
                    $"orphan_from_managed={orphanFromManaged}, orphan_from_native={orphanFromNative}, " +
                    $"orphan_to_managed={orphanToManaged}, orphan_to_native={orphanToNative}, unknown_kinds={unknownKinds}, " +
                    $"orphan_allocation_region_refs={orphanAllocationRegionRefs}, orphan_region_first_allocation_refs={orphanRegionFirstAllocationRefs}");
            }
        }
    }

    #endregion

    #region Helpers

    private static void Exec(DuckDBConnection connection, string sql)
    {
        // DuckDB doesn't support multiple statements in one ExecuteNonQuery call;
        // split on semicolons and run each statement individually.
        foreach (var stmt in sql.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = stmt;
            cmd.ExecuteNonQuery();
        }
    }

    private static long QueryCount(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return Convert.ToInt64(result);
    }

    #endregion

    #region Schema

    // Column types must match C# value types passed to the Appender exactly
    // (DuckDB Appender reads raw bytes; passing int to BIGINT column corrupts data).
    // int  → INTEGER (32-bit), long/ulong-cast → BIGINT (64-bit).
    private const string SchemaTablesScript = """
CREATE OR REPLACE TABLE snapshot_info (
    snapshot_path VARCHAR NOT NULL,
    exported_at_utc VARCHAR NOT NULL,
    unity_version VARCHAR,
    snap_format_version INTEGER,
    session_guid BIGINT,
    product_name VARCHAR,
    platform VARCHAR,
    record_date_utc VARCHAR
);

CREATE OR REPLACE TABLE native_objects (
    native_object_index INTEGER PRIMARY KEY,
    instance_id VARCHAR,
    name VARCHAR,
    size_bytes BIGINT NOT NULL,
    native_object_address BIGINT NOT NULL DEFAULT 0,
    root_reference_id BIGINT NOT NULL DEFAULT -1,
    type_index INTEGER,
    native_type_name VARCHAR,
    is_destroyed BOOLEAN NOT NULL,
    resident_size_bytes BIGINT
);

CREATE OR REPLACE TABLE managed_objects (
    managed_object_index INTEGER PRIMARY KEY,
    address BIGINT NOT NULL,
    size_bytes BIGINT NOT NULL,
    type_index INTEGER,
    managed_type_name VARCHAR,
    native_object_index BIGINT
);

CREATE OR REPLACE TABLE connections (
    from_kind VARCHAR NOT NULL,
    from_index BIGINT NOT NULL,
    to_kind VARCHAR NOT NULL,
    to_index BIGINT NOT NULL,
    connection_type VARCHAR NOT NULL
);

CREATE OR REPLACE TABLE native_roots (
    root_index INTEGER PRIMARY KEY,
    root_id BIGINT NOT NULL,
    area_name VARCHAR,
    object_name VARCHAR,
    accumulated_size_bytes BIGINT NOT NULL,
    resident_size_bytes BIGINT
);

CREATE OR REPLACE TABLE memory_regions (
    region_index INTEGER PRIMARY KEY,
    address_base BIGINT NOT NULL,
    address_size BIGINT NOT NULL,
    name VARCHAR,
    parent_region_index INTEGER,
    first_allocation_index INTEGER,
    num_allocations INTEGER NOT NULL
);

CREATE OR REPLACE TABLE native_allocations (
    allocation_index INTEGER PRIMARY KEY,
    address BIGINT NOT NULL,
    size_bytes BIGINT NOT NULL,
    overhead_size_bytes BIGINT NOT NULL,
    padding_size_bytes BIGINT NOT NULL,
    memory_region_index INTEGER,
    root_reference_id BIGINT
);

CREATE OR REPLACE TABLE system_memory_regions (
    region_index INTEGER PRIMARY KEY,
    address BIGINT NOT NULL,
    size_bytes BIGINT NOT NULL,
    resident_bytes BIGINT NOT NULL,
    type INTEGER NOT NULL,
    name VARCHAR
);

CREATE OR REPLACE TABLE summary_metrics (
    metric_group VARCHAR NOT NULL,
    category VARCHAR NOT NULL,
    committed_bytes BIGINT NOT NULL,
    resident_bytes BIGINT NOT NULL,
    resident_available INTEGER NOT NULL
);
""";

    private const string CreateIndexesScript = """
CREATE INDEX idx_connections_from ON connections(from_kind, from_index);
CREATE INDEX idx_connections_to ON connections(to_kind, to_index);
CREATE INDEX idx_native_objects_instance_id ON native_objects(instance_id);
CREATE INDEX idx_native_objects_is_destroyed ON native_objects(is_destroyed);
CREATE INDEX idx_managed_objects_address ON managed_objects(address);
CREATE INDEX idx_memory_regions_address_base ON memory_regions(address_base);
CREATE INDEX idx_native_allocations_address ON native_allocations(address);
CREATE INDEX idx_native_allocations_region ON native_allocations(memory_region_index);
""";

    #endregion
}

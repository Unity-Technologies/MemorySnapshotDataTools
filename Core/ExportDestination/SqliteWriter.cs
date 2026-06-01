using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using MemorySnapshotDataTools;

namespace MemorySnapshotDataTools.ExportDestination;

/// <summary>
/// Static helper for writing snapshot data to SQLite: schema creation, bulk inserts from <see cref="WriteBatch"/> queue,
/// and optional validation (row counts and referential integrity).
/// Used by <see cref="SqliteExportDestination"/>.
/// </summary>
internal static class SqliteWriter
{
    private const int MaxSqlParametersPerStatement = 900;
    private const int DefaultRowsPerBulkInsert = 128;

    #region Validation

    /// <summary>
    /// Validates the database at <paramref name="dbPath"/>: for minimal mode checks row counts against <paramref name="rawData"/>;
    /// for full mode also checks primary key uniqueness and connection/region/allocation referential integrity.
    /// </summary>
    /// <param name="dbPath">Path to the SQLite database file.</param>
    /// <param name="rawData">Expected snapshot data for count comparison.</param>
    /// <param name="mode">Validation level (none, minimal, full).</param>
    /// <exception cref="InvalidOperationException">If counts or referential checks fail.</exception>
    public static void Validate(string dbPath, RawSnapshotData rawData, ValidationMode mode)
    {
        if (mode == ValidationMode.None)
            return;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
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
            throw new InvalidOperationException("SQLite validation count mismatch between extracted rows and persisted rows.");
        }

        if (mode == ValidationMode.Full)
        {
            // Quick full-mode sanity check on key uniqueness and not-null semantics.
            var duplicateNativeKeys = QueryCount(connection, "SELECT COUNT(*) FROM (SELECT native_object_index, COUNT(*) c FROM native_objects GROUP BY native_object_index HAVING c > 1);");
            var duplicateManagedKeys = QueryCount(connection, "SELECT COUNT(*) FROM (SELECT managed_object_index, COUNT(*) c FROM managed_objects GROUP BY managed_object_index HAVING c > 1);");
            var duplicateRegionKeys = QueryCount(connection, "SELECT COUNT(*) FROM (SELECT region_index, COUNT(*) c FROM memory_regions GROUP BY region_index HAVING c > 1);");
            var duplicateAllocationKeys = QueryCount(connection, "SELECT COUNT(*) FROM (SELECT allocation_index, COUNT(*) c FROM native_allocations GROUP BY allocation_index HAVING c > 1);");
            if (duplicateNativeKeys > 0 || duplicateManagedKeys > 0 || duplicateRegionKeys > 0 || duplicateAllocationKeys > 0)
                throw new InvalidOperationException("SQLite validation failed: duplicate primary key rows found.");

            var orphanFromManaged = QueryCount(connection, """
                SELECT COUNT(*) FROM connections c
                WHERE c.from_kind = 'managed_object'
                  AND NOT EXISTS (
                    SELECT 1
                    FROM managed_objects m
                    WHERE m.managed_object_index = c.from_index
                  );
                """);
            var orphanFromNative = QueryCount(connection, """
                SELECT COUNT(*) FROM connections c
                WHERE c.from_kind = 'native_object'
                  AND NOT EXISTS (
                    SELECT 1
                    FROM native_objects n
                    WHERE n.native_object_index = c.from_index
                  );
                """);
            var orphanToManaged = QueryCount(connection, """
                SELECT COUNT(*) FROM connections c
                WHERE c.to_kind = 'managed_object'
                  AND NOT EXISTS (
                    SELECT 1
                    FROM managed_objects m
                    WHERE m.managed_object_index = c.to_index
                  );
                """);
            var orphanToNative = QueryCount(connection, """
                SELECT COUNT(*) FROM connections c
                WHERE c.to_kind = 'native_object'
                  AND NOT EXISTS (
                    SELECT 1
                    FROM native_objects n
                    WHERE n.native_object_index = c.to_index
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
                    SELECT 1
                    FROM memory_regions r
                    WHERE r.region_index = a.memory_region_index
                  );
                """);
            var orphanRegionFirstAllocationRefs = QueryCount(connection, """
                SELECT COUNT(*) FROM memory_regions r
                WHERE r.first_allocation_index IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1
                    FROM native_allocations a
                    WHERE a.allocation_index = r.first_allocation_index
                  );
                """);

            if (orphanFromManaged > 0 || orphanFromNative > 0 || orphanToManaged > 0 || orphanToNative > 0 || unknownKinds > 0 ||
                orphanAllocationRegionRefs > 0 || orphanRegionFirstAllocationRefs > 0)
            {
                throw new InvalidOperationException(
                    $"SQLite validation failed: invalid graph or memory-map references. " +
                    $"orphan_from_managed={orphanFromManaged}, orphan_from_native={orphanFromNative}, " +
                    $"orphan_to_managed={orphanToManaged}, orphan_to_native={orphanToNative}, unknown_kinds={unknownKinds}, " +
                    $"orphan_allocation_region_refs={orphanAllocationRegionRefs}, orphan_region_first_allocation_refs={orphanRegionFirstAllocationRefs}");
            }
        }
    }

    #endregion

    #region SummaryMetrics

    /// <summary>
    /// Inserts the MemoryProfiler summary metrics into the <c>summary_metrics</c> table (created by the schema script).
    /// </summary>
    public static void WriteSummaryMetrics(string dbPath, SummaryMetrics metrics)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO summary_metrics(metric_group, category, committed_bytes, resident_bytes, resident_available)
            VALUES ($g, $c, $cb, $rb, $ra);
            """;
        var g = cmd.Parameters.Add("$g", Microsoft.Data.Sqlite.SqliteType.Text);
        var c = cmd.Parameters.Add("$c", Microsoft.Data.Sqlite.SqliteType.Text);
        var cb = cmd.Parameters.Add("$cb", Microsoft.Data.Sqlite.SqliteType.Integer);
        var rb = cmd.Parameters.Add("$rb", Microsoft.Data.Sqlite.SqliteType.Integer);
        var ra = cmd.Parameters.Add("$ra", Microsoft.Data.Sqlite.SqliteType.Integer);

        foreach (var (group, category, committed, resident, residentAvailable) in SummaryMetricsTable.Enumerate(metrics))
        {
            g.Value = group;
            c.Value = category;
            cb.Value = unchecked((long)committed);
            rb.Value = unchecked((long)resident);
            ra.Value = residentAvailable ? 1 : 0;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    #endregion

    #region ConsumeAndWrite

    /// <summary>
    /// Consumes batches from the queue, writes all tables to the SQLite database, and returns per-table row counts and timings.
    /// Creates the directory for <paramref name="dbPath"/> if needed, enables WAL mode, and runs schema creation and bulk inserts inside a transaction.
    /// </summary>
    /// <param name="dbPath">Output database file path.</param>
    /// <param name="snapshotInfo">Metadata to insert into snapshot_info.</param>
    /// <param name="queue">Bounded queue of write batches.</param>
    /// <param name="state">Shared pipeline state to update.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>Per-table row counts and insert/commit/index timings.</returns>
    public static WriteStats ConsumeAndWrite(
        string dbPath,
        SnapshotInfo snapshotInfo,
        BlockingCollection<WriteBatch> queue,
        PipelineState state,
        CancellationToken token)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        Exec(connection, null, "PRAGMA journal_mode=WAL;");
        Exec(connection, null, "PRAGMA synchronous=NORMAL;");
        Exec(connection, null, "PRAGMA temp_store=MEMORY;");
        Exec(connection, null, "PRAGMA cache_size=-200000;");

        var stats = new WriteStats();

        using var transaction = connection.BeginTransaction();
        try
        {
            ExecScript(connection, transaction, SchemaTablesScript);

            using var snapshotCmd = connection.CreateCommand();
            snapshotCmd.Transaction = transaction;
            snapshotCmd.CommandText = """
                INSERT INTO snapshot_info(
                    snapshot_path, exported_at_utc, unity_version,
                    snap_format_version, session_guid, product_name, platform, record_date_utc)
                VALUES ($p, $e, $u, $sf, $sg, $pn, $pl, $rd);
                """;
            snapshotCmd.Parameters.AddWithValue("$p", snapshotInfo.SnapshotPath);
            snapshotCmd.Parameters.AddWithValue("$e", snapshotInfo.ExportedAtUtc);
            snapshotCmd.Parameters.AddWithValue("$u", snapshotInfo.UnityVersion);
            snapshotCmd.Parameters.AddWithValue("$sf", snapshotInfo.SnapFormatVersion == 0 ? DBNull.Value : snapshotInfo.SnapFormatVersion);
            snapshotCmd.Parameters.AddWithValue("$sg", snapshotInfo.SessionGuid == 0 ? DBNull.Value : unchecked((long)snapshotInfo.SessionGuid));
            snapshotCmd.Parameters.AddWithValue("$pn", string.IsNullOrEmpty(snapshotInfo.ProductName) ? DBNull.Value : snapshotInfo.ProductName);
            snapshotCmd.Parameters.AddWithValue("$pl", string.IsNullOrEmpty(snapshotInfo.Platform) ? DBNull.Value : snapshotInfo.Platform);
            snapshotCmd.Parameters.AddWithValue("$rd", string.IsNullOrEmpty(snapshotInfo.RecordDateUtc) ? DBNull.Value : snapshotInfo.RecordDateUtc);
            snapshotCmd.ExecuteNonQuery();
            state.AddWritten(1);
            var insertSw = Stopwatch.StartNew();
            using var nativeCmd = PrepareNativeInsert(connection, transaction);
            using var managedCmd = PrepareManagedInsert(connection, transaction);
            using var connectionCmd = PrepareConnectionInsert(connection, transaction);
            using var rootCmd = PrepareRootInsert(connection, transaction);
            using var regionCmd = PrepareRegionInsert(connection, transaction);
            using var allocationCmd = PrepareAllocationInsert(connection, transaction);
            using var systemRegionCmd = PrepareSystemRegionInsert(connection, transaction);

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
                            nativeCmd.Parameters[0].Value = row.NativeObjectIndex;
                            nativeCmd.Parameters[1].Value = row.InstanceId ?? string.Empty;
                            nativeCmd.Parameters[2].Value = row.Name ?? string.Empty;
                            nativeCmd.Parameters[3].Value = unchecked((long)row.SizeBytes);
                            nativeCmd.Parameters[4].Value = unchecked((long)row.NativeObjectAddress);
                            nativeCmd.Parameters[5].Value = row.RootReferenceId;
                            nativeCmd.Parameters[6].Value = row.TypeIndex;
                            nativeCmd.Parameters[7].Value = row.NativeTypeName ?? string.Empty;
                            nativeCmd.Parameters[8].Value = row.IsDestroyed ? 1 : 0;
                            nativeCmd.Parameters[9].Value = row.ResidentSizeBytes.HasValue
                                ? unchecked((long)row.ResidentSizeBytes.Value)
                                : DBNull.Value;
                            nativeCmd.ExecuteNonQuery();
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
                            managedCmd.Parameters[0].Value = row.ManagedObjectIndex;
                            managedCmd.Parameters[1].Value = unchecked((long)row.Address);
                            managedCmd.Parameters[2].Value = row.SizeBytes;
                            managedCmd.Parameters[3].Value = row.TypeIndex;
                            managedCmd.Parameters[4].Value = row.ManagedTypeName ?? string.Empty;
                            managedCmd.Parameters[5].Value = row.NativeObjectIndex >= 0 ? row.NativeObjectIndex : DBNull.Value;
                            managedCmd.ExecuteNonQuery();
                        }
                        managedSw.Stop();
                        stats.ManagedObjectRows += batch.ManagedObjects.Length;
                        stats.ManagedObjectInsertMs += managedSw.ElapsedMilliseconds;
                        state.AddWritten(batch.ManagedObjects.Length);
                        break;

                    case WriteBatchKind.Connections:
                        var connectionSw = Stopwatch.StartNew();
                        foreach (var row in batch.Connections)
                        {
                            connectionCmd.Parameters[0].Value = row.FromKind ?? string.Empty;
                            connectionCmd.Parameters[1].Value = row.FromIndex;
                            connectionCmd.Parameters[2].Value = row.ToKind ?? string.Empty;
                            connectionCmd.Parameters[3].Value = row.ToIndex;
                            connectionCmd.Parameters[4].Value = row.ConnectionType ?? string.Empty;
                            connectionCmd.ExecuteNonQuery();
                        }
                        connectionSw.Stop();
                        stats.ConnectionRows += batch.Connections.Length;
                        stats.ConnectionInsertMs += connectionSw.ElapsedMilliseconds;
                        state.AddWritten(batch.Connections.Length);
                        break;

                    case WriteBatchKind.NativeRoots:
                        var rootSw = Stopwatch.StartNew();
                        foreach (var row in batch.NativeRoots)
                        {
                            rootCmd.Parameters[0].Value = row.RootIndex;
                            rootCmd.Parameters[1].Value = row.RootId;
                            rootCmd.Parameters[2].Value = row.AreaName ?? string.Empty;
                            rootCmd.Parameters[3].Value = row.ObjectName ?? string.Empty;
                            rootCmd.Parameters[4].Value = unchecked((long)row.AccumulatedSizeBytes);
                            rootCmd.Parameters[5].Value = row.ResidentSizeBytes.HasValue
                                ? unchecked((long)row.ResidentSizeBytes.Value)
                                : DBNull.Value;
                            rootCmd.ExecuteNonQuery();
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
                            regionCmd.Parameters[0].Value = row.RegionIndex;
                            regionCmd.Parameters[1].Value = unchecked((long)row.AddressBase);
                            regionCmd.Parameters[2].Value = unchecked((long)row.AddressSize);
                            regionCmd.Parameters[3].Value = row.Name ?? string.Empty;
                            regionCmd.Parameters[4].Value = row.ParentRegionIndex >= 0 ? row.ParentRegionIndex : DBNull.Value;
                            regionCmd.Parameters[5].Value = row.FirstAllocationIndex >= 0 ? row.FirstAllocationIndex : DBNull.Value;
                            regionCmd.Parameters[6].Value = row.NumAllocations;
                            regionCmd.ExecuteNonQuery();
                        }
                        regionSw.Stop();
                        stats.MemoryRegionRows += batch.MemoryRegions.Length;
                        stats.MemoryRegionInsertMs += regionSw.ElapsedMilliseconds;
                        state.AddWritten(batch.MemoryRegions.Length);
                        break;

                    case WriteBatchKind.NativeAllocations:
                        var allocationSw = Stopwatch.StartNew();
                        foreach (var row in batch.NativeAllocations)
                        {
                            allocationCmd.Parameters[0].Value = row.AllocationIndex;
                            allocationCmd.Parameters[1].Value = unchecked((long)row.Address);
                            allocationCmd.Parameters[2].Value = unchecked((long)row.SizeBytes);
                            allocationCmd.Parameters[3].Value = unchecked((long)row.OverheadSizeBytes);
                            allocationCmd.Parameters[4].Value = unchecked((long)row.PaddingSizeBytes);
                            allocationCmd.Parameters[5].Value = row.MemoryRegionIndex >= 0 ? row.MemoryRegionIndex : DBNull.Value;
                            allocationCmd.Parameters[6].Value = row.RootReferenceId >= 0 ? row.RootReferenceId : DBNull.Value;
                            allocationCmd.ExecuteNonQuery();
                        }
                        allocationSw.Stop();
                        stats.NativeAllocationRows += batch.NativeAllocations.Length;
                        stats.NativeAllocationInsertMs += allocationSw.ElapsedMilliseconds;
                        state.AddWritten(batch.NativeAllocations.Length);
                        break;

                    case WriteBatchKind.SystemMemoryRegions:
                        var sysSw = Stopwatch.StartNew();
                        foreach (var row in batch.SystemMemoryRegions)
                        {
                            systemRegionCmd.Parameters[0].Value = row.RegionIndex;
                            systemRegionCmd.Parameters[1].Value = unchecked((long)row.Address);
                            systemRegionCmd.Parameters[2].Value = unchecked((long)row.SizeBytes);
                            systemRegionCmd.Parameters[3].Value = unchecked((long)row.ResidentBytes);
                            systemRegionCmd.Parameters[4].Value = row.Type;
                            systemRegionCmd.Parameters[5].Value = row.Name ?? string.Empty;
                            systemRegionCmd.ExecuteNonQuery();
                        }
                        sysSw.Stop();
                        stats.SystemMemoryRegionRows += batch.SystemMemoryRegions.Length;
                        stats.SystemMemoryRegionInsertMs += sysSw.ElapsedMilliseconds;
                        state.AddWritten(batch.SystemMemoryRegions.Length);
                        break;
                }
            }
            insertSw.Stop();
            stats.TotalInsertMs = insertSw.ElapsedMilliseconds;

            var commitSw = Stopwatch.StartNew();
            transaction.Commit();
            commitSw.Stop();
            stats.CommitMs = commitSw.ElapsedMilliseconds;

            var indexSw = Stopwatch.StartNew();
            using (var indexTransaction = connection.BeginTransaction())
            {
                ExecScript(connection, indexTransaction, CreateIndexesScript);
                indexTransaction.Commit();
            }
            indexSw.Stop();
            stats.IndexBuildMs = indexSw.ElapsedMilliseconds;
            return stats;
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch
            {
                // Keep original failure.
            }
            throw;
        }
    }

    #endregion

    #region Schema

    private static int RowsPerStatement(int columnCount)
    {
        var byParams = Math.Max(1, MaxSqlParametersPerStatement / Math.Max(1, columnCount));
        return Math.Max(1, Math.Min(DefaultRowsPerBulkInsert, byParams));
    }

    private static SqliteCommand PrepareNativeInsert(SqliteConnection connection, SqliteTransaction tx)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO native_objects(native_object_index, instance_id, name, size_bytes, native_object_address, root_reference_id, type_index, native_type_name, is_destroyed, resident_size_bytes) VALUES ($i, $id, $n, $s, $addr, $rid, $t, $tn, $d, $r);";
        _ = command.Parameters.Add("$i", SqliteType.Integer);
        _ = command.Parameters.Add("$id", SqliteType.Text);
        _ = command.Parameters.Add("$n", SqliteType.Text);
        _ = command.Parameters.Add("$s", SqliteType.Integer);
        _ = command.Parameters.Add("$addr", SqliteType.Integer);
        _ = command.Parameters.Add("$rid", SqliteType.Integer);
        _ = command.Parameters.Add("$t", SqliteType.Integer);
        _ = command.Parameters.Add("$tn", SqliteType.Text);
        _ = command.Parameters.Add("$d", SqliteType.Integer);
        _ = command.Parameters.Add("$r", SqliteType.Integer);
        return command;
    }

    private static SqliteCommand PrepareManagedInsert(SqliteConnection connection, SqliteTransaction tx)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO managed_objects(managed_object_index, address, size_bytes, type_index, managed_type_name, native_object_index) VALUES ($i, $a, $s, $t, $tn, $ni);";
        _ = command.Parameters.Add("$i", SqliteType.Integer);
        _ = command.Parameters.Add("$a", SqliteType.Integer);
        _ = command.Parameters.Add("$s", SqliteType.Integer);
        _ = command.Parameters.Add("$t", SqliteType.Integer);
        _ = command.Parameters.Add("$tn", SqliteType.Text);
        _ = command.Parameters.Add("$ni", SqliteType.Integer);
        return command;
    }

    private static SqliteCommand PrepareConnectionInsert(SqliteConnection connection, SqliteTransaction tx)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO connections(from_kind, from_index, to_kind, to_index, connection_type) VALUES ($fk, $fi, $tk, $ti, $ct);";
        _ = command.Parameters.Add("$fk", SqliteType.Text);
        _ = command.Parameters.Add("$fi", SqliteType.Integer);
        _ = command.Parameters.Add("$tk", SqliteType.Text);
        _ = command.Parameters.Add("$ti", SqliteType.Integer);
        _ = command.Parameters.Add("$ct", SqliteType.Text);
        return command;
    }

    private static SqliteCommand PrepareRootInsert(SqliteConnection connection, SqliteTransaction tx)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO native_roots(root_index, root_id, area_name, object_name, accumulated_size_bytes, resident_size_bytes) VALUES ($i, $rid, $a, $o, $s, $r);";
        _ = command.Parameters.Add("$i", SqliteType.Integer);
        _ = command.Parameters.Add("$rid", SqliteType.Integer);
        _ = command.Parameters.Add("$a", SqliteType.Text);
        _ = command.Parameters.Add("$o", SqliteType.Text);
        _ = command.Parameters.Add("$s", SqliteType.Integer);
        _ = command.Parameters.Add("$r", SqliteType.Integer);
        return command;
    }

    private static SqliteCommand PrepareRegionInsert(SqliteConnection connection, SqliteTransaction tx)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO memory_regions(region_index, address_base, address_size, name, parent_region_index, first_allocation_index, num_allocations) VALUES ($i, $ab, $as, $n, $p, $f, $c);";
        _ = command.Parameters.Add("$i", SqliteType.Integer);
        _ = command.Parameters.Add("$ab", SqliteType.Integer);
        _ = command.Parameters.Add("$as", SqliteType.Integer);
        _ = command.Parameters.Add("$n", SqliteType.Text);
        _ = command.Parameters.Add("$p", SqliteType.Integer);
        _ = command.Parameters.Add("$f", SqliteType.Integer);
        _ = command.Parameters.Add("$c", SqliteType.Integer);
        return command;
    }

    private static SqliteCommand PrepareAllocationInsert(SqliteConnection connection, SqliteTransaction tx)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO native_allocations(allocation_index, address, size_bytes, overhead_size_bytes, padding_size_bytes, memory_region_index, root_reference_id) VALUES ($i, $a, $s, $o, $p, $mr, $rid);";
        _ = command.Parameters.Add("$i", SqliteType.Integer);
        _ = command.Parameters.Add("$a", SqliteType.Integer);
        _ = command.Parameters.Add("$s", SqliteType.Integer);
        _ = command.Parameters.Add("$o", SqliteType.Integer);
        _ = command.Parameters.Add("$p", SqliteType.Integer);
        _ = command.Parameters.Add("$mr", SqliteType.Integer);
        _ = command.Parameters.Add("$rid", SqliteType.Integer);
        return command;
    }

    private static SqliteCommand PrepareSystemRegionInsert(SqliteConnection connection, SqliteTransaction tx)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO system_memory_regions(region_index, address, size_bytes, resident_bytes, type, name) VALUES ($i, $a, $s, $r, $t, $n);";
        _ = command.Parameters.Add("$i", SqliteType.Integer);
        _ = command.Parameters.Add("$a", SqliteType.Integer);
        _ = command.Parameters.Add("$s", SqliteType.Integer);
        _ = command.Parameters.Add("$r", SqliteType.Integer);
        _ = command.Parameters.Add("$t", SqliteType.Integer);
        _ = command.Parameters.Add("$n", SqliteType.Text);
        return command;
    }

    #endregion

    #region Bulk insert

    private static SqliteCommand CreateBulkInsertCommand(
        SqliteConnection connection,
        SqliteTransaction tx,
        string insertPrefix,
        int rowCount,
        int columnCount)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        var sql = new StringBuilder(insertPrefix.Length + rowCount * (columnCount * 6 + 3));
        sql.Append(insertPrefix);
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                sql.Append(',');

            sql.Append('(');
            for (var col = 0; col < columnCount; col++)
            {
                if (col > 0)
                    sql.Append(',');
                sql.Append("$p").Append(row * columnCount + col);
            }
            sql.Append(')');
        }

        command.CommandText = sql.ToString();
        return command;
    }

    private static void WriteNativeObjectRows(SqliteConnection connection, SqliteTransaction tx, NativeObjectRow[] rows)
    {
        const int cols = 10;
        const string insertPrefix = "INSERT INTO native_objects(native_object_index, instance_id, name, size_bytes, native_object_address, root_reference_id, type_index, native_type_name, is_destroyed, resident_size_bytes) VALUES ";
        var rowsPerStatement = RowsPerStatement(cols);
        for (var start = 0; start < rows.Length; start += rowsPerStatement)
        {
            var count = Math.Min(rowsPerStatement, rows.Length - start);
            using var command = CreateBulkInsertCommand(connection, tx, insertPrefix, count, cols);
            for (var i = 0; i < count; i++)
            {
                var row = rows[start + i];
                var p = i * cols;
                command.Parameters.AddWithValue($"$p{p}", row.NativeObjectIndex);
                command.Parameters.AddWithValue($"$p{p + 1}", row.InstanceId ?? string.Empty);
                command.Parameters.AddWithValue($"$p{p + 2}", row.Name ?? string.Empty);
                command.Parameters.AddWithValue($"$p{p + 3}", unchecked((long)row.SizeBytes));
                command.Parameters.AddWithValue($"$p{p + 4}", unchecked((long)row.NativeObjectAddress));
                command.Parameters.AddWithValue($"$p{p + 5}", row.RootReferenceId);
                command.Parameters.AddWithValue($"$p{p + 6}", row.TypeIndex);
                command.Parameters.AddWithValue($"$p{p + 7}", row.NativeTypeName ?? string.Empty);
                command.Parameters.AddWithValue($"$p{p + 8}", row.IsDestroyed ? 1 : 0);
                command.Parameters.AddWithValue($"$p{p + 9}", row.ResidentSizeBytes.HasValue ? unchecked((long)row.ResidentSizeBytes.Value) : DBNull.Value);
            }
            command.ExecuteNonQuery();
        }
    }

    private static void WriteManagedObjectRows(SqliteConnection connection, SqliteTransaction tx, ManagedObjectRow[] rows)
    {
        const int cols = 6;
        const string insertPrefix = "INSERT INTO managed_objects(managed_object_index, address, size_bytes, type_index, managed_type_name, native_object_index) VALUES ";
        var rowsPerStatement = RowsPerStatement(cols);
        for (var start = 0; start < rows.Length; start += rowsPerStatement)
        {
            var count = Math.Min(rowsPerStatement, rows.Length - start);
            using var command = CreateBulkInsertCommand(connection, tx, insertPrefix, count, cols);
            for (var i = 0; i < count; i++)
            {
                var row = rows[start + i];
                var p = i * cols;
                command.Parameters.AddWithValue($"$p{p}", row.ManagedObjectIndex);
                command.Parameters.AddWithValue($"$p{p + 1}", unchecked((long)row.Address));
                command.Parameters.AddWithValue($"$p{p + 2}", row.SizeBytes);
                command.Parameters.AddWithValue($"$p{p + 3}", row.TypeIndex);
                command.Parameters.AddWithValue($"$p{p + 4}", row.ManagedTypeName ?? string.Empty);
                command.Parameters.AddWithValue($"$p{p + 5}", row.NativeObjectIndex >= 0 ? row.NativeObjectIndex : DBNull.Value);
            }
            command.ExecuteNonQuery();
        }
    }

    private static void WriteConnectionRows(SqliteConnection connection, SqliteTransaction tx, ConnectionRow[] rows)
    {
        const int cols = 5;
        const string insertPrefix = "INSERT INTO connections(from_kind, from_index, to_kind, to_index, connection_type) VALUES ";
        var rowsPerStatement = RowsPerStatement(cols);
        for (var start = 0; start < rows.Length; start += rowsPerStatement)
        {
            var count = Math.Min(rowsPerStatement, rows.Length - start);
            using var command = CreateBulkInsertCommand(connection, tx, insertPrefix, count, cols);
            for (var i = 0; i < count; i++)
            {
                var row = rows[start + i];
                var p = i * cols;
                command.Parameters.AddWithValue($"$p{p}", row.FromKind ?? string.Empty);
                command.Parameters.AddWithValue($"$p{p + 1}", row.FromIndex);
                command.Parameters.AddWithValue($"$p{p + 2}", row.ToKind ?? string.Empty);
                command.Parameters.AddWithValue($"$p{p + 3}", row.ToIndex);
                command.Parameters.AddWithValue($"$p{p + 4}", row.ConnectionType ?? string.Empty);
            }
            command.ExecuteNonQuery();
        }
    }

    private static void WriteNativeRootRows(SqliteConnection connection, SqliteTransaction tx, NativeRootRow[] rows)
    {
        const int cols = 6;
        const string insertPrefix = "INSERT INTO native_roots(root_index, root_id, area_name, object_name, accumulated_size_bytes, resident_size_bytes) VALUES ";
        var rowsPerStatement = RowsPerStatement(cols);
        for (var start = 0; start < rows.Length; start += rowsPerStatement)
        {
            var count = Math.Min(rowsPerStatement, rows.Length - start);
            using var command = CreateBulkInsertCommand(connection, tx, insertPrefix, count, cols);
            for (var i = 0; i < count; i++)
            {
                var row = rows[start + i];
                var p = i * cols;
                command.Parameters.AddWithValue($"$p{p}", row.RootIndex);
                command.Parameters.AddWithValue($"$p{p + 1}", row.RootId);
                command.Parameters.AddWithValue($"$p{p + 2}", row.AreaName ?? string.Empty);
                command.Parameters.AddWithValue($"$p{p + 3}", row.ObjectName ?? string.Empty);
                command.Parameters.AddWithValue($"$p{p + 4}", unchecked((long)row.AccumulatedSizeBytes));
                command.Parameters.AddWithValue($"$p{p + 5}", row.ResidentSizeBytes.HasValue ? unchecked((long)row.ResidentSizeBytes.Value) : DBNull.Value);
            }
            command.ExecuteNonQuery();
        }
    }

    private static void WriteMemoryRegionRows(SqliteConnection connection, SqliteTransaction tx, MemoryRegionRow[] rows)
    {
        const int cols = 7;
        const string insertPrefix = "INSERT INTO memory_regions(region_index, address_base, address_size, name, parent_region_index, first_allocation_index, num_allocations) VALUES ";
        var rowsPerStatement = RowsPerStatement(cols);
        for (var start = 0; start < rows.Length; start += rowsPerStatement)
        {
            var count = Math.Min(rowsPerStatement, rows.Length - start);
            using var command = CreateBulkInsertCommand(connection, tx, insertPrefix, count, cols);
            for (var i = 0; i < count; i++)
            {
                var row = rows[start + i];
                var p = i * cols;
                command.Parameters.AddWithValue($"$p{p}", row.RegionIndex);
                command.Parameters.AddWithValue($"$p{p + 1}", unchecked((long)row.AddressBase));
                command.Parameters.AddWithValue($"$p{p + 2}", unchecked((long)row.AddressSize));
                command.Parameters.AddWithValue($"$p{p + 3}", row.Name ?? string.Empty);
                command.Parameters.AddWithValue($"$p{p + 4}", row.ParentRegionIndex >= 0 ? row.ParentRegionIndex : DBNull.Value);
                command.Parameters.AddWithValue($"$p{p + 5}", row.FirstAllocationIndex >= 0 ? row.FirstAllocationIndex : DBNull.Value);
                command.Parameters.AddWithValue($"$p{p + 6}", row.NumAllocations);
            }
            command.ExecuteNonQuery();
        }
    }

    private static void WriteNativeAllocationRows(SqliteConnection connection, SqliteTransaction tx, NativeAllocationRow[] rows)
    {
        const int cols = 7;
        const string insertPrefix = "INSERT INTO native_allocations(allocation_index, address, size_bytes, overhead_size_bytes, padding_size_bytes, memory_region_index, root_reference_id) VALUES ";
        var rowsPerStatement = RowsPerStatement(cols);
        for (var start = 0; start < rows.Length; start += rowsPerStatement)
        {
            var count = Math.Min(rowsPerStatement, rows.Length - start);
            using var command = CreateBulkInsertCommand(connection, tx, insertPrefix, count, cols);
            for (var i = 0; i < count; i++)
            {
                var row = rows[start + i];
                var p = i * cols;
                command.Parameters.AddWithValue($"$p{p}", row.AllocationIndex);
                command.Parameters.AddWithValue($"$p{p + 1}", unchecked((long)row.Address));
                command.Parameters.AddWithValue($"$p{p + 2}", unchecked((long)row.SizeBytes));
                command.Parameters.AddWithValue($"$p{p + 3}", unchecked((long)row.OverheadSizeBytes));
                command.Parameters.AddWithValue($"$p{p + 4}", unchecked((long)row.PaddingSizeBytes));
                command.Parameters.AddWithValue($"$p{p + 5}", row.MemoryRegionIndex >= 0 ? row.MemoryRegionIndex : DBNull.Value);
                command.Parameters.AddWithValue($"$p{p + 6}", row.RootReferenceId >= 0 ? row.RootReferenceId : DBNull.Value);
            }
            command.ExecuteNonQuery();
        }
    }

    private static void WriteSystemMemoryRegionRows(SqliteConnection connection, SqliteTransaction tx, SystemMemoryRegionRow[] rows)
    {
        const int cols = 6;
        const string insertPrefix = "INSERT INTO system_memory_regions(region_index, address, size_bytes, resident_bytes, type, name) VALUES ";
        var rowsPerStatement = RowsPerStatement(cols);
        for (var start = 0; start < rows.Length; start += rowsPerStatement)
        {
            var count = Math.Min(rowsPerStatement, rows.Length - start);
            using var command = CreateBulkInsertCommand(connection, tx, insertPrefix, count, cols);
            for (var i = 0; i < count; i++)
            {
                var row = rows[start + i];
                var p = i * cols;
                command.Parameters.AddWithValue($"$p{p}", row.RegionIndex);
                command.Parameters.AddWithValue($"$p{p + 1}", unchecked((long)row.Address));
                command.Parameters.AddWithValue($"$p{p + 2}", unchecked((long)row.SizeBytes));
                command.Parameters.AddWithValue($"$p{p + 3}", unchecked((long)row.ResidentBytes));
                command.Parameters.AddWithValue($"$p{p + 4}", row.Type);
                command.Parameters.AddWithValue($"$p{p + 5}", row.Name ?? string.Empty);
            }
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region Helpers

    private static void Exec(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecScript(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long QueryCount(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return Convert.ToInt64(result);
    }

    #endregion

    private const string SchemaTablesScript = """
DROP TABLE IF EXISTS snapshot_info;
DROP TABLE IF EXISTS native_objects;
DROP TABLE IF EXISTS managed_objects;
DROP TABLE IF EXISTS connections;
DROP TABLE IF EXISTS native_roots;
DROP TABLE IF EXISTS memory_regions;
DROP TABLE IF EXISTS native_allocations;
DROP TABLE IF EXISTS system_memory_regions;

CREATE TABLE snapshot_info (
    snapshot_path TEXT NOT NULL,
    exported_at_utc TEXT NOT NULL,
    unity_version TEXT,
    snap_format_version INTEGER,
    session_guid INTEGER,
    product_name TEXT,
    platform TEXT,
    record_date_utc TEXT
);

CREATE TABLE native_objects (
    native_object_index INTEGER PRIMARY KEY,
    instance_id TEXT,
    name TEXT,
    size_bytes INTEGER NOT NULL,
    native_object_address INTEGER NOT NULL DEFAULT 0,
    root_reference_id INTEGER NOT NULL DEFAULT -1,
    type_index INTEGER,
    native_type_name TEXT,
    is_destroyed INTEGER NOT NULL DEFAULT 0,
    resident_size_bytes INTEGER
);

CREATE TABLE managed_objects (
    managed_object_index INTEGER PRIMARY KEY,
    address INTEGER NOT NULL,
    size_bytes INTEGER NOT NULL,
    type_index INTEGER,
    managed_type_name TEXT,
    native_object_index INTEGER
);

CREATE TABLE connections (
    from_kind TEXT NOT NULL,
    from_index INTEGER NOT NULL,
    to_kind TEXT NOT NULL,
    to_index INTEGER NOT NULL,
    connection_type TEXT NOT NULL
);

CREATE TABLE native_roots (
    root_index INTEGER PRIMARY KEY,
    root_id INTEGER NOT NULL,
    area_name TEXT,
    object_name TEXT,
    accumulated_size_bytes INTEGER NOT NULL,
    resident_size_bytes INTEGER
);

CREATE TABLE memory_regions (
    region_index INTEGER PRIMARY KEY,
    address_base INTEGER NOT NULL,
    address_size INTEGER NOT NULL,
    name TEXT,
    parent_region_index INTEGER,
    first_allocation_index INTEGER,
    num_allocations INTEGER NOT NULL
);

CREATE TABLE native_allocations (
    allocation_index INTEGER PRIMARY KEY,
    address INTEGER NOT NULL,
    size_bytes INTEGER NOT NULL,
    overhead_size_bytes INTEGER NOT NULL,
    padding_size_bytes INTEGER NOT NULL,
    memory_region_index INTEGER,
    root_reference_id INTEGER
);

CREATE TABLE system_memory_regions (
    region_index INTEGER PRIMARY KEY,
    address INTEGER NOT NULL,
    size_bytes INTEGER NOT NULL,
    resident_bytes INTEGER NOT NULL,
    type INTEGER NOT NULL,
    name TEXT
);

CREATE TABLE summary_metrics (
    metric_group TEXT NOT NULL,
    category TEXT NOT NULL,
    committed_bytes INTEGER NOT NULL,
    resident_bytes INTEGER NOT NULL,
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
}


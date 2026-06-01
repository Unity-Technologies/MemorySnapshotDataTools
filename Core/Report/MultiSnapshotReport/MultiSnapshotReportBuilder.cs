using System.Globalization;
using DuckDB.NET.Data;
using MemorySnapshotDataTools.Parser;
using MemorySnapshotDataTools.Validation;
using Microsoft.Data.Sqlite;

namespace MemorySnapshotDataTools.Report.MultiSnapshotReport;

/// <summary>
/// Queries a set of DuckDB or SQLite files for memory metrics on specific native types and roots,
/// then builds a <see cref="MultiSnapshotReportModel"/> grouped by capture session.
/// </summary>
public static class MultiSnapshotReportBuilder
{
    private static readonly string[] TrackedNativeTypes = ["AssetBundle", "SerializedFile"];

    /// <summary>
    /// Scans <paramref name="directory"/> for database files matching <paramref name="nameFilter"/>,
    /// queries each for tracked metrics, and returns a grouped report model.
    /// </summary>
    /// <param name="directory">Directory containing .duckdb or .db files.</param>
    /// <param name="nameFilter">Optional case-insensitive substring filter on filenames.</param>
    /// <param name="title">Report title.</param>
    /// <returns>Populated multi-snapshot report model.</returns>
    public static MultiSnapshotReportModel Build(string directory, string? nameFilter, string title)
    {
        var dbPaths = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(p =>
            {
                var ext = Path.GetExtension(p);
                return ext.Equals(".duckdb", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".db", StringComparison.OrdinalIgnoreCase);
            })
            .Where(p => string.IsNullOrWhiteSpace(nameFilter)
                || Path.GetFileName(p).Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<SnapshotMetricsRow>();
        foreach (var dbPath in dbPaths)
        {
            try
            {
                rows.Add(QueryDatabase(dbPath));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Skipping {Path.GetFileName(dbPath)}: {ex.Message}");
            }
        }

        var sessions = MultiSnapshotSessionGrouper.BuildGroups(rows);

        return new MultiSnapshotReportModel
        {
            Title = title,
            GeneratedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC",
            SourceDirectory = Path.GetFullPath(directory),
            Sessions = sessions,
        };
    }

    private static SnapshotMetricsRow QueryDatabase(string dbPath)
    {
        var ext = Path.GetExtension(dbPath);
        return ext.Equals(".db", StringComparison.OrdinalIgnoreCase)
            ? QuerySqlite(dbPath)
            : QueryDuckDb(dbPath);
    }

    private static SnapshotMetricsRow QueryDuckDb(string dbPath)
    {
        // Read-only: this path only queries metrics, never writes. See docs/sql-safety.md.
        using var connection = new DuckDBConnection($"Data Source={dbPath};ACCESS_MODE=READ_ONLY");
        connection.Open();

        var snapshotMeta = QuerySnapshotMetadata(connection, isDuckDb: true);
        var nativeTypes = QueryNativeTypes(connection, isDuckDb: true);
        var remapperRoots = QueryRemapperRoots(connection, isDuckDb: true);
        return BuildRow(dbPath, nativeTypes, remapperRoots, snapshotMeta);
    }

    private static SnapshotMetricsRow QuerySqlite(string dbPath)
    {
        // Read-only: this path only queries metrics, never writes. See docs/sql-safety.md.
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();

        var snapshotMeta = QuerySnapshotMetadata(connection, isDuckDb: false);
        var nativeTypes = QueryNativeTypes(connection, isDuckDb: false);
        var remapperRoots = QueryRemapperRoots(connection, isDuckDb: false);
        return BuildRow(dbPath, nativeTypes, remapperRoots, snapshotMeta);
    }

    private static Dictionary<string, NativeTypeSnapshotMetrics> QueryNativeTypes(object connection, bool isDuckDb)
    {
        var result = new Dictionary<string, NativeTypeSnapshotMetrics>(StringComparer.Ordinal);
        foreach (var typeName in TrackedNativeTypes)
        {
            result[typeName] = new NativeTypeSnapshotMetrics
            {
                NativeTypeName = typeName,
                Count = 0,
                AllocatedBytes = 0,
                ResidentBytes = 0,
            };
        }

        var nullResidentExpr = isDuckDb ? "CAST(NULL AS BIGINT)" : "NULL";
        var objectResidentExpr = HasColumn(connection, isDuckDb, "native_objects", "resident_size_bytes")
            ? "COALESCE(SUM(resident_size_bytes), 0)"
            : nullResidentExpr;
        var rootResidentExpr = HasColumn(connection, isDuckDb, "native_roots", "resident_size_bytes")
            ? "COALESCE(SUM(resident_size_bytes), 0)"
            : nullResidentExpr;

        // The resident expressions above are a closed set of hard-coded SQL fragments chosen by HasColumn;
        // the native type name is a value, so it is bound as a parameter (DuckDB '?', SQLite '$nativeType').
        var nativeTypeParam = isDuckDb ? "?" : "$nativeType";
        var assetBundleSql = $"""
            SELECT COUNT(*) AS obj_count,
                   COALESCE(SUM(size_bytes), 0) AS allocated_bytes,
                   {objectResidentExpr} AS resident_bytes
            FROM native_objects
            WHERE native_type_name = {nativeTypeParam}
              AND is_destroyed = false
            """;

        var serializedFileSql = $"""
            SELECT COUNT(*) AS obj_count,
                   COALESCE(SUM(accumulated_size_bytes), 0) AS allocated_bytes,
                   {rootResidentExpr} AS resident_bytes
            FROM native_roots
            WHERE {GoldenValidationQueries.SerializedFileAreaPredicate}
            """;

        ReadNativeTypeAggregate(connection, isDuckDb, assetBundleSql, GoldenValidationQueries.AssetBundleNativeTypeName, result,
            ("$nativeType", GoldenValidationQueries.AssetBundleNativeTypeName));
        ReadNativeTypeAggregate(connection, isDuckDb, serializedFileSql, GoldenValidationQueries.SerializedFileMetricName, result);
        return result;
    }

    // Table/column names are bound as parameters rather than interpolated, so this never builds SQL
    // by concatenating identifiers. DuckDB queries information_schema.columns (a regular table that
    // accepts bind parameters, matching DuckDbReportQueries.HasColumn); SQLite uses pragma_table_info
    // with named parameters, matching SqliteReportQueries.HasColumn.
    private static bool HasColumn(object connection, bool isDuckDb, string tableName, string columnName)
    {
        if (isDuckDb)
        {
            using var cmd = ((DuckDBConnection)connection).CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM information_schema.columns WHERE table_schema = 'main' AND table_name = ? AND column_name = ? LIMIT 1";
            cmd.Parameters.Add(new DuckDBParameter { Value = tableName });
            cmd.Parameters.Add(new DuckDBParameter { Value = columnName });
            return cmd.ExecuteScalar() != null;
        }

        using var sqliteCmd = ((SqliteConnection)connection).CreateCommand();
        sqliteCmd.CommandText = "SELECT 1 FROM pragma_table_info($t) WHERE name = $c LIMIT 1";
        sqliteCmd.Parameters.AddWithValue("$t", tableName);
        sqliteCmd.Parameters.AddWithValue("$c", columnName);
        return sqliteCmd.ExecuteScalar() != null;
    }

    private static void ReadNativeTypeAggregate(
        object connection,
        bool isDuckDb,
        string sql,
        string typeName,
        Dictionary<string, NativeTypeSnapshotMetrics> result,
        params (string Name, object Value)[] parameters)
    {
        if (isDuckDb)
        {
            using var cmd = ((DuckDBConnection)connection).CreateCommand();
            cmd.CommandText = sql;
            // DuckDB binds positionally ('?'), in declaration order.
            foreach (var (_, value) in parameters)
                cmd.Parameters.Add(new DuckDBParameter { Value = value });
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return;

            result[typeName] = new NativeTypeSnapshotMetrics
            {
                NativeTypeName = typeName,
                Count = DbScalarReader.GetInt32(reader, 0),
                AllocatedBytes = DbScalarReader.GetInt64(reader, 1),
                ResidentBytes = reader.IsDBNull(2) ? null : DbScalarReader.GetInt64(reader, 2),
            };
            return;
        }

        using var sqliteCmd = ((SqliteConnection)connection).CreateCommand();
        sqliteCmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            sqliteCmd.Parameters.AddWithValue(name, value);
        using var sqliteReader = sqliteCmd.ExecuteReader();
        if (!sqliteReader.Read())
            return;

        result[typeName] = new NativeTypeSnapshotMetrics
        {
            NativeTypeName = typeName,
            Count = sqliteReader.GetInt32(0),
            AllocatedBytes = sqliteReader.GetInt64(1),
            ResidentBytes = sqliteReader.IsDBNull(2) ? null : sqliteReader.GetInt64(2),
        };
    }

    private static List<NativeRootSnapshotMetrics> QueryRemapperRoots(object connection, bool isDuckDb)
    {
        var hasRootResident = HasColumn(connection, isDuckDb, "native_roots", "resident_size_bytes");
        var residentSelect = hasRootResident
            ? "resident_size_bytes"
            : isDuckDb ? "CAST(NULL AS BIGINT) AS resident_size_bytes" : "NULL AS resident_size_bytes";
        var sql = $"""
            SELECT area_name, object_name,
                   accumulated_size_bytes AS allocated_bytes,
                   {residentSelect}
            FROM native_roots
            WHERE object_name LIKE '%Remapper%'
               OR (COALESCE(area_name, '') || ':' || COALESCE(object_name, '')) LIKE '%PersistentManager%Remapper%'
            """;

        var roots = new List<NativeRootSnapshotMetrics>();
        if (isDuckDb)
        {
            using var cmd = ((DuckDBConnection)connection).CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                roots.Add(ReadRootRow(reader));
            }
        }
        else
        {
            using var cmd = ((SqliteConnection)connection).CreateCommand();
            cmd.CommandText = sql.Replace("COALESCE", "IFNULL", StringComparison.Ordinal);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                roots.Add(ReadRootRow(reader));
            }
        }

        return roots;
    }

    private static NativeRootSnapshotMetrics ReadRootRow(System.Data.Common.DbDataReader reader) =>
        new()
        {
            AreaName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            ObjectName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            AllocatedBytes = DbScalarReader.GetInt64(reader, 2),
            ResidentBytes = reader.IsDBNull(3) ? null : DbScalarReader.GetInt64(reader, 3),
        };

    private static SnapshotMetricsRow BuildRow(
        string dbPath,
        Dictionary<string, NativeTypeSnapshotMetrics> nativeTypes,
        List<NativeRootSnapshotMetrics> remapperRoots,
        DbSnapshotMetadata dbMeta)
    {
        var fileName = Path.GetFileNameWithoutExtension(dbPath);
        var meta = EnrichMetadata(dbPath, fileName, dbMeta);
        var filenameSession = MultiSnapshotSessionKey.FromFileName(fileName, meta.UnityVersionDisplay);

        var row = new SnapshotMetricsRow
        {
            SnapshotName = fileName,
            DatabasePath = dbPath,
            SessionKey = filenameSession.SessionKey,
            CaptureDate = filenameSession.CaptureDate,
            UnityVersion = meta.UnityVersionDisplay,
            SnapFormatVersion = meta.SnapFormatVersion,
            SessionGuid = meta.SessionGuid,
            ProductName = meta.ProductName,
            Platform = meta.Platform,
            PlatformKind = CapturePlatformKindExtensions.FromPlatformName(meta.Platform),
            SortTimestamp = meta.SortTimestamp,
            NativeTypes = nativeTypes,
            RemapperRoots = remapperRoots,
        };

        row = row with { SessionKey = MultiSnapshotSessionGrouper.BuildClusterKey(row) };
        return row;
    }

    private static EnrichedSnapshotMetadata EnrichMetadata(string dbPath, string fileName, DbSnapshotMetadata dbMeta)
    {
        var needsSnap = dbMeta.SessionGuid == 0
            || string.IsNullOrWhiteSpace(dbMeta.Platform)
            || string.IsNullOrWhiteSpace(dbMeta.ProductName)
            || (string.IsNullOrWhiteSpace(dbMeta.UnityVersion)
                || dbMeta.UnityVersion.StartsWith("format:", StringComparison.OrdinalIgnoreCase));

        CaptureMetadata? snapMeta = null;
        if (needsSnap)
        {
            var snapPath = Path.ChangeExtension(dbPath, ".snap");
            if (File.Exists(snapPath))
            {
                try
                {
                    snapMeta = SnapMetadataReader.Read(snapPath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Metadata read failed for {Path.GetFileName(snapPath)}: {ex.Message}");
                }
            }
        }

        var sessionGuid = dbMeta.SessionGuid != 0 ? dbMeta.SessionGuid : snapMeta?.SessionGuid ?? 0;
        var productName = !string.IsNullOrWhiteSpace(dbMeta.ProductName) ? dbMeta.ProductName : snapMeta?.ProductName ?? string.Empty;
        var platform = !string.IsNullOrWhiteSpace(dbMeta.Platform) ? dbMeta.Platform : snapMeta?.Platform ?? string.Empty;
        var unityVersion = dbMeta.UnityVersion ?? string.Empty;
        if (string.IsNullOrWhiteSpace(unityVersion) || unityVersion.StartsWith("format:", StringComparison.OrdinalIgnoreCase))
            unityVersion = snapMeta?.UnityVersion ?? unityVersion;

        var snapFormat = dbMeta.SnapFormatVersion != 0 ? dbMeta.SnapFormatVersion : snapMeta?.SnapFormatVersion ?? 0;
        if (snapFormat == 0 && unityVersion.StartsWith("format:", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(unityVersion["format:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            snapFormat = parsed;
        }

        var sortTimestamp = dbMeta.RecordDateUtc
            ?? snapMeta?.RecordDateUtc
            ?? ParseCaptureDateFromFileName(fileName);

        return new EnrichedSnapshotMetadata
        {
            SessionGuid = sessionGuid,
            ProductName = productName,
            Platform = platform,
            UnityVersionDisplay = unityVersion,
            SnapFormatVersion = snapFormat,
            SortTimestamp = sortTimestamp,
        };
    }

    private static DateTime ParseCaptureDateFromFileName(string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            fileName,
            @"_(?<date>\d{4}-\d{2}-\d{2})_(?<time>\d{2}-\d{2}-\d{2})",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success)
            return DateTime.MinValue;

        var text = $"{match.Groups["date"].Value} {match.Groups["time"].Value.Replace('-', ':')}";
        return DateTime.TryParseExact(
            text,
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private static DbSnapshotMetadata QuerySnapshotMetadata(object connection, bool isDuckDb)
    {
        var hasSessionGuid = HasColumn(connection, isDuckDb, "snapshot_info", "session_guid");
        var sql = hasSessionGuid
            ? """
              SELECT unity_version, snap_format_version, session_guid, product_name, platform, record_date_utc
              FROM snapshot_info LIMIT 1
              """
            : "SELECT unity_version FROM snapshot_info LIMIT 1";

        if (isDuckDb)
        {
            using var cmd = ((DuckDBConnection)connection).CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadDbMetadata(reader, hasSessionGuid) : new DbSnapshotMetadata();
        }

        using var sqliteCmd = ((SqliteConnection)connection).CreateCommand();
        sqliteCmd.CommandText = sql;
        using var sqliteReader = sqliteCmd.ExecuteReader();
        return sqliteReader.Read() ? ReadDbMetadata(sqliteReader, hasSessionGuid) : new DbSnapshotMetadata();
    }

    private static DbSnapshotMetadata ReadDbMetadata(System.Data.Common.DbDataReader reader, bool extended)
    {
        var meta = new DbSnapshotMetadata
        {
            UnityVersion = reader.IsDBNull(0) ? null : reader.GetString(0),
        };

        if (!extended)
            return meta;

        meta = meta with
        {
            SnapFormatVersion = reader.IsDBNull(1) ? 0 : Convert.ToUInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
            SessionGuid = reader.IsDBNull(2) ? 0 : ToUInt32SessionGuid(reader.GetValue(2)),
            ProductName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Platform = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
        };

        if (!reader.IsDBNull(5)
            && DateTime.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var recordDate))
        {
            meta = meta with { RecordDateUtc = recordDate.ToUniversalTime() };
        }

        return meta;
    }

    private static uint ToUInt32SessionGuid(object value) =>
        unchecked((uint)Convert.ToInt64(value, CultureInfo.InvariantCulture));

    private sealed record DbSnapshotMetadata
    {
        public string? UnityVersion { get; init; }
        public uint SnapFormatVersion { get; init; }
        public uint SessionGuid { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public string Platform { get; init; } = string.Empty;
        public DateTime? RecordDateUtc { get; init; }
    }

    private sealed record EnrichedSnapshotMetadata
    {
        public uint SessionGuid { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public string Platform { get; init; } = string.Empty;
        public string UnityVersionDisplay { get; init; } = string.Empty;
        public uint SnapFormatVersion { get; init; }
        public DateTime SortTimestamp { get; init; }
    }
}

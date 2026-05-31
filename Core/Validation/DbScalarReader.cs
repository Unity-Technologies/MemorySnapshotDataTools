using System.Globalization;
using System.Numerics;

namespace MemorySnapshotDataTools.Validation;

/// <summary>
/// Converts scalar values from DuckDB/SQLite readers (DuckDB often returns <see cref="BigInteger"/>).
/// </summary>
internal static class DbScalarReader
{
    /// <summary>
    /// Reads a 32-bit integer from a data reader column.
    /// </summary>
    public static int GetInt32(System.Data.Common.DbDataReader reader, int ordinal) =>
        ToInt32(reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal));

    /// <summary>
    /// Reads a 64-bit integer from a data reader column.
    /// </summary>
    public static long GetInt64(System.Data.Common.DbDataReader reader, int ordinal) =>
        ToInt64(reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal));

    /// <summary>
    /// Converts a database scalar to <see cref="int"/>.
    /// </summary>
    public static int ToInt32(object? value)
    {
        if (value == null || value is DBNull)
            return 0;

        return value switch
        {
            int i => i,
            long l => checked((int)l),
            uint u => (int)u,
            ulong ul => checked((int)ul),
            short s => s,
            byte b => b,
            BigInteger bi => checked((int)bi),
            decimal d => (int)d,
            double d => (int)d,
            float f => (int)f,
            string s => int.Parse(s, CultureInfo.InvariantCulture),
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Converts a database scalar to <see cref="long"/>.
    /// </summary>
    public static long ToInt64(object? value)
    {
        if (value == null || value is DBNull)
            return 0;

        return value switch
        {
            long l => l,
            int i => i,
            uint u => u,
            ulong ul => (long)ul,
            short s => s,
            byte b => b,
            BigInteger bi => (long)bi,
            decimal d => (long)d,
            double d => (long)d,
            float f => (long)f,
            string s => long.Parse(s, CultureInfo.InvariantCulture),
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        };
    }
}

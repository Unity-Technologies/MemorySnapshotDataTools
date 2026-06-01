using MemorySnapshotDataTools;

namespace MemorySnapshotDataTools.Parser;

/// <summary>
/// Reads capture metadata (session, platform, Unity version) from a <c>.snap</c> file without full decode.
/// </summary>
public static class SnapMetadataReader
{
    /// <summary>
    /// Reads metadata from the snapshot at <paramref name="snapPath"/>.
    /// </summary>
    public static CaptureMetadata Read(string snapPath)
    {
        using var reader = SnapReader.Open(snapPath);
        return Read(reader, snapPath);
    }

    /// <summary>
    /// Reads metadata from an open <see cref="SnapReader"/>.
    /// </summary>
    internal static CaptureMetadata Read(SnapReader reader, string? snapPath = null)
    {
        var formatVersion = reader.ReadMetadataVersion();
        var ticks = reader.ReadMetadataRecordDateTicks();
        DateTime? recordDate = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;

        var metadata = SnapProfileTargetInfoParser.TryRead(reader, formatVersion)
                       ?? TryReadLegacyPlatform(reader)
                       ?? new CaptureMetadata();

        metadata = metadata with
        {
            SnapFormatVersion = formatVersion,
            RecordDateUtc = recordDate,
        };

        if (string.IsNullOrWhiteSpace(metadata.Platform) && !string.IsNullOrWhiteSpace(snapPath))
            metadata = metadata with { Platform = InferPlatformFromFileName(snapPath) };

        return metadata;
    }

    /// <summary>
    /// Infers runtime platform from snapshot filename tokens (IOS, Android, etc.).
    /// </summary>
    public static string InferPlatformFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Contains("IOS", StringComparison.OrdinalIgnoreCase)
            || name.Contains("IPhone", StringComparison.OrdinalIgnoreCase))
        {
            return "IPhonePlayer";
        }

        if (name.Contains("Android", StringComparison.OrdinalIgnoreCase))
            return "Android";

        return string.Empty;
    }

    private static CaptureMetadata? TryReadLegacyPlatform(SnapReader reader)
    {
        if (!reader.HasEntry(SnapEntryType.Metadata_UserMetadata))
            return null;

        var blob = reader.ReadSingleElementBytes(SnapEntryType.Metadata_UserMetadata);
        if (blob.Length < 8)
            return null;

        try
        {
            return TryParseLegacyUserMetadata(blob);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Legacy user metadata: int32 description length, UTF-16 description, int32 platform length, UTF-16 platform.
    /// </summary>
    private static CaptureMetadata? TryParseLegacyUserMetadata(byte[] buffer)
    {
        var offset = 0;
        offset = SkipUtf16String(buffer, offset, out _);
        if (offset < 0 || offset + 4 > buffer.Length)
            return null;

        var platformLen = BitConverter.ToInt32(buffer, offset);
        offset += 4;
        if (platformLen <= 0 || offset + platformLen * 2 > buffer.Length)
            return new CaptureMetadata { Platform = "Unknown Platform" };

        var platform = System.Text.Encoding.Unicode.GetString(buffer, offset, platformLen * 2);
        return new CaptureMetadata { Platform = platform };
    }

    private static int SkipUtf16String(byte[] buffer, int offset, out string value)
    {
        value = string.Empty;
        if (offset + 4 > buffer.Length)
            return -1;

        var len = BitConverter.ToInt32(buffer, offset);
        offset += 4;
        if (len == 0)
            return offset;

        if (offset + len * 2 > buffer.Length)
            return -1;

        value = System.Text.Encoding.Unicode.GetString(buffer, offset, len * 2);
        return offset + len * 2;
    }
}

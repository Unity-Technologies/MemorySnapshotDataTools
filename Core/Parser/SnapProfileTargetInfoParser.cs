using System.Buffers.Binary;
using System.Text;
using MemorySnapshotDataTools;

namespace MemorySnapshotDataTools.Parser;

/// <summary>
/// Parses the 512-byte <c>ProfileTarget_Info</c> blob from Unity memory snapshots.
/// Layout matches Unity Memory Profiler <c>ProfileTargetInfo</c>.
/// </summary>
internal static class SnapProfileTargetInfoParser
{
    private const int StructSize = 512;
    private const int OffSessionGuid = 0;
    private const int OffRuntimePlatform = 4;
    private const int OffUnityVersionLength = 48;
    private const int OffUnityVersionBuffer = 52;
    private const int OffProductNameLength = 68;
    private const int OffProductNameBuffer = 72;

    /// <summary>Minimum snap format that may include profile target info.</summary>
    public const uint MinFormatWithProfileTarget = 11;

    /// <summary>
    /// Parses profile target bytes when at least <see cref="StructSize"/> bytes are available.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out CaptureMetadata metadata)
    {
        metadata = new CaptureMetadata();
        if (data.Length < OffProductNameBuffer)
            return false;

        var sessionGuid = BinaryPrimitives.ReadUInt32LittleEndian(data[OffSessionGuid..]);
        var runtimePlatform = BinaryPrimitives.ReadInt32LittleEndian(data[OffRuntimePlatform..]);
        var unityLen = BinaryPrimitives.ReadUInt32LittleEndian(data[OffUnityVersionLength..]);
        var productLen = BinaryPrimitives.ReadUInt32LittleEndian(data[OffProductNameLength..]);

        if (unityLen == 0 || unityLen > 16 || productLen == 0 || productLen > 256)
            return false;

        var unityVersion = ReadUtf8String(data, OffUnityVersionBuffer, 16, unityLen);
        var productName = ReadUtf8String(data, OffProductNameBuffer, 256, productLen);
        if (string.IsNullOrWhiteSpace(unityVersion) || string.IsNullOrWhiteSpace(productName))
            return false;

        var platform = RuntimePlatformToName(runtimePlatform);
        if (platform.StartsWith("Platform_", StringComparison.Ordinal) && sessionGuid == 0)
            return false;

        metadata = new CaptureMetadata
        {
            SessionGuid = sessionGuid,
            ProductName = productName,
            UnityVersion = unityVersion,
            Platform = platform,
        };
        return true;
    }

    /// <summary>Reads profile target info from a snapshot reader when present.</summary>
    public static CaptureMetadata? TryRead(SnapReader reader, uint formatVersion)
    {
        if (formatVersion < MinFormatWithProfileTarget)
            return null;

        CaptureMetadata? best = null;

        if (reader.HasEntry(SnapEntryType.ProfileTarget_Info))
        {
            best = TryReadEntryBlob(reader, SnapEntryType.ProfileTarget_Info);
        }

        if (best is { HasProfilerSession: true })
            return best;

        for (ushort i = 0; i < 128; i++)
        {
            var entryType = (SnapEntryType)i;
            if (entryType == SnapEntryType.ProfileTarget_Info)
                continue;
            if (!reader.HasEntry(entryType))
                continue;

            var candidate = TryReadEntryBlob(reader, entryType);
            if (candidate is null)
                continue;

            if (candidate.HasProfilerSession)
                return candidate;

            best ??= candidate;
        }

        return best;
    }

    private static CaptureMetadata? TryReadEntryBlob(SnapReader reader, SnapEntryType entryType)
    {
        try
        {
            var bytes = reader.ReadSingleElementBytes(entryType);
            if (bytes.Length < StructSize)
                return null;

            for (var start = 0; start <= bytes.Length - StructSize; start += 4)
            {
                if (TryParse(bytes.AsSpan(start, StructSize), out var meta))
                    return meta;
            }
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    private static string ReadUtf8String(ReadOnlySpan<byte> data, int offset, int maxLen, uint length)
    {
        if (length == 0 || length > maxLen)
            return string.Empty;
        if (offset + length > data.Length)
            return string.Empty;
        return Encoding.UTF8.GetString(data.Slice(offset, (int)length));
    }

    private static string RuntimePlatformToName(int runtimePlatform) => runtimePlatform switch
    {
        8 => "IPhonePlayer",
        11 => "Android",
        31 => "tvOS",
        9 => "PS4",
        38 => "PS5",
        2 => "WindowsPlayer",
        1 => "OSXPlayer",
        13 => "LinuxPlayer",
        0 => "OSXEditor",
        7 => "WindowsEditor",
        12 => "LinuxEditor",
        _ => $"Platform_{runtimePlatform}",
    };
}

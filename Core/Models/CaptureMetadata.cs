namespace MemorySnapshotDataTools;

/// <summary>
/// Capture-time metadata from a Unity memory snapshot file (profile target, session, platform).
/// </summary>
public sealed record CaptureMetadata
{
    /// <summary>Invalid session GUID when not present in the snapshot.</summary>
    public const uint InvalidSessionGuid = 0;

    /// <summary>Session identifier shared by snapshots taken in the same player run.</summary>
    public uint SessionGuid { get; init; }

    /// <summary>Project or product name from the capture target.</summary>
    public string ProductName { get; init; } = string.Empty;

    /// <summary>Unity editor/player version string (e.g. 6000.3.11f1).</summary>
    public string UnityVersion { get; init; } = string.Empty;

    /// <summary>Runtime platform name (e.g. IPhonePlayer, Android).</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>Snapshot file format version from <c>Metadata_Version</c>.</summary>
    public uint SnapFormatVersion { get; init; }

    /// <summary>Capture timestamp from metadata, if available.</summary>
    public DateTime? RecordDateUtc { get; init; }

    /// <summary>Whether a non-zero session GUID was read from the file.</summary>
    public bool HasProfilerSession => SessionGuid != InvalidSessionGuid;

    /// <summary>Normalized platform for UI (iOS, Android, or other).</summary>
    public CapturePlatformKind PlatformKind => CapturePlatformKindExtensions.FromPlatformName(Platform);
}

/// <summary>Coarse platform bucket for report icons and labels.</summary>
public enum CapturePlatformKind
{
    Unknown,
    IOS,
    Android,
    Other,
}

/// <summary>Maps Unity runtime platform strings to <see cref="CapturePlatformKind"/>.</summary>
public static class CapturePlatformKindExtensions
{
    /// <summary>Classifies a platform string from snapshot metadata.</summary>
    public static CapturePlatformKind FromPlatformName(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return CapturePlatformKind.Unknown;

        var p = platform.AsSpan().Trim();
        if (p.Contains("IPhone", StringComparison.OrdinalIgnoreCase)
            || p.Contains("iOS", StringComparison.OrdinalIgnoreCase)
            || p.Equals("tvOS", StringComparison.OrdinalIgnoreCase))
        {
            return CapturePlatformKind.IOS;
        }

        if (p.Contains("Android", StringComparison.OrdinalIgnoreCase))
            return CapturePlatformKind.Android;

        return CapturePlatformKind.Other;
    }

    /// <summary>Short label for session headers.</summary>
    public static string ToDisplayLabel(this CapturePlatformKind kind) => kind switch
    {
        CapturePlatformKind.IOS => "iOS",
        CapturePlatformKind.Android => "Android",
        CapturePlatformKind.Other => "Other",
        _ => string.Empty,
    };
}

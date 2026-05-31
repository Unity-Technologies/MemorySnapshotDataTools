using System.Globalization;
using System.Text.RegularExpressions;

namespace MemorySnapshotDataTools.Report.MultiSnapshotReport;

/// <summary>
/// Parsed filename and database metadata used to group multi-snapshot report rows.
/// </summary>
public readonly record struct MultiSnapshotSessionMetadata
{
    /// <summary>Stable grouping key (project, date, capture context, Unity/snap format).</summary>
    public string SessionKey { get; init; }

    /// <summary>Human-readable session title for report section headers.</summary>
    public string DisplayTitle { get; init; }

    /// <summary>Project or capture prefix from the filename (e.g. MyGame).</summary>
    public string ProjectPrefix { get; init; }

    /// <summary>Capture date from the filename (YYYY-MM-DD).</summary>
    public string CaptureDate { get; init; }

    /// <summary>Raw capture context suffix from the filename, if any.</summary>
    public string CaptureContext { get; init; }
}

/// <summary>
/// Builds session keys for multi-snapshot reports from snapshot filenames and <c>snapshot_info</c>.
/// </summary>
public static class MultiSnapshotSessionKey
{
    private static readonly Regex SnapshotFileNameRegex = new(
        @"^(?<prefix>.+?)_(?<date>\d{4}-\d{2}-\d{2})_(?<time>\d{2}-\d{2}-\d{2})(?:_(?<context>.+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Derives session metadata from a snapshot database basename and optional Unity/snap format version.
    /// </summary>
    public static MultiSnapshotSessionMetadata FromFileName(string fileName, string? unityVersion)
    {
        var match = SnapshotFileNameRegex.Match(fileName);
        var prefix = match.Success ? match.Groups["prefix"].Value.Trim() : fileName;
        var date = match.Success ? match.Groups["date"].Value : string.Empty;
        var time = match.Success ? match.Groups["time"].Value : string.Empty;
        var context = match.Success && match.Groups["context"].Success
            ? match.Groups["context"].Value.Trim()
            : string.Empty;

        var unity = string.IsNullOrWhiteSpace(unityVersion) ? string.Empty : unityVersion.Trim();
        // Unlabeled captures share only a date; use capture time so unrelated runs are not one session.
        var contextKey = string.IsNullOrEmpty(context)
            ? (string.IsNullOrEmpty(time) ? "_general" : time)
            : context;
        var unityKey = string.IsNullOrEmpty(unity) ? "unknown" : unity;

        var sessionKey = string.IsNullOrEmpty(date)
            ? $"{prefix}|{contextKey}|{unityKey}"
            : $"{prefix}|{date}|{contextKey}|{unityKey}";

        var displayParts = new List<string>();
        if (!string.IsNullOrEmpty(prefix))
            displayParts.Add(prefix);
        if (!string.IsNullOrEmpty(date))
            displayParts.Add(date);
        if (string.IsNullOrEmpty(context) && !string.IsNullOrEmpty(time))
            displayParts.Add(FormatCaptureTimeLabel(time));
        var contextLabel = FormatCaptureContextLabel(context);
        if (!string.IsNullOrEmpty(contextLabel))
            displayParts.Add(contextLabel);
        var unityLabel = FormatUnityVersionLabel(unity);
        if (!string.IsNullOrEmpty(unityLabel))
            displayParts.Add(unityLabel);

        var displayTitle = displayParts.Count > 0
            ? string.Join(" · ", displayParts)
            : fileName;

        return new MultiSnapshotSessionMetadata
        {
            SessionKey = sessionKey,
            DisplayTitle = displayTitle,
            ProjectPrefix = prefix,
            CaptureDate = date,
            CaptureContext = context,
        };
    }

    /// <summary>
    /// Converts a session key to a safe HTML element id.
    /// </summary>
    public static string ToHtmlId(string sessionKey)
    {
        var id = sessionKey.Replace("|", "-", StringComparison.Ordinal);
        foreach (var c in Path.GetInvalidFileNameChars())
            id = id.Replace(c.ToString(), "-", StringComparison.Ordinal);
        return id;
    }

    private static string FormatCaptureTimeLabel(string time)
    {
        if (DateTime.TryParseExact(
                time,
                "HH-mm-ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return time.Replace('-', ':');
    }

    private static string FormatCaptureContextLabel(string context)
    {
        if (string.IsNullOrEmpty(context))
            return "General capture";

        var tokens = context.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return context;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Equals("IOS", StringComparison.OrdinalIgnoreCase))
                tokens[i] = "iOS";
            else if (token.Equals("Android", StringComparison.OrdinalIgnoreCase))
                tokens[i] = "Android";
            else if (token.Length > 0)
                tokens[i] = char.ToUpperInvariant(token[0]) + token[1..];
        }

        return string.Join(' ', tokens);
    }

    private static string FormatUnityVersionLabel(string unityVersion)
    {
        if (string.IsNullOrEmpty(unityVersion))
            return string.Empty;

        if (unityVersion.StartsWith("format:", StringComparison.OrdinalIgnoreCase))
        {
            var format = unityVersion["format:".Length..].Trim();
            return string.IsNullOrEmpty(format) ? string.Empty : $"Snap format {format}";
        }

        return unityVersion;
    }
}

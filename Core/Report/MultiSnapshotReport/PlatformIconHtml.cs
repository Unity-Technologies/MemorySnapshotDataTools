namespace MemorySnapshotDataTools.Report.MultiSnapshotReport;

/// <summary>
/// Inline SVG platform icons for multi-snapshot report rows (iOS / Android).
/// </summary>
internal static class PlatformIconHtml
{
    /// <summary>Returns an HTML span with a platform icon, or empty when unknown.</summary>
    public static string Render(CapturePlatformKind kind, string? title = null)
    {
        var (svg, cssClass, defaultTitle) = kind switch
        {
            CapturePlatformKind.IOS => (AppleSvg, "ios", "iOS"),
            CapturePlatformKind.Android => (AndroidSvg, "android", "Android"),
            _ => (null, null, null),
        };

        if (svg is null)
            return string.Empty;

        var tip = System.Net.WebUtility.HtmlEncode(title ?? defaultTitle ?? string.Empty);
        return $"<span class=\"platform-icon {cssClass}\" title=\"{tip}\" aria-hidden=\"true\">{svg}</span>";
    }

    private const string AppleSvg = """
        <svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor" aria-hidden="true">
        <path d="M16.52 12.21c.02-2.17 1.77-3.21 1.85-3.26-1.01-1.47-2.58-1.67-3.14-1.69-1.34-.14-2.62.79-3.3.79-.68 0-1.73-.77-2.85-.75-1.47.02-2.82.85-3.57 2.16-1.53 2.65-.39 6.57 1.1 8.72.73 1.05 1.6 2.23 2.75 2.19 1.1-.04 1.52-.71 2.85-.71 1.33 0 1.7.71 2.86.69 1.18-.02 1.93-1.07 2.65-2.12.84-1.22 1.18-2.4 1.2-2.46-.03-.01-2.3-.88-2.32-3.5zm-2.1-6.8c.61-.74 1.02-1.77.91-2.8-.88.04-1.94.59-2.57 1.32-.56.65-1.05 1.69-.87 2.69.92.07 1.86-.47 2.53-1.21z"/>
        </svg>
        """;

    private const string AndroidSvg = """
        <svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor" aria-hidden="true">
        <path d="M6 11c-.55 0-1 .45-1 1v5c0 .55.45 1 1 1s1-.45 1-1v-5c0-.55-.45-1-1-1zm12 0c-.55 0-1 .45-1 1v5c0 .55.45 1 1 1s1-.45 1-1v-5c0-.55-.45-1-1-1zM8.5 4.8 7.4 3.1a.75.75 0 1 0-1.3.75l1.1 1.7A6.97 6.97 0 0 0 6 6.5C4.07 7.56 3 9.61 3 12h18c0-2.39-1.07-4.44-3-5.5a6.97 6.97 0 0 0-1.2-.95l1.1-1.7a.75.75 0 0 0-1.3-.75l-1.1 1.7A7.04 7.04 0 0 0 12 5c-.86 0-1.68.15-2.4.42l-1.1-1.62zM9 9.5a1 1 0 1 0 0-2 1 1 0 0 0 0 2zm6 0a1 1 0 1 0 0-2 1 1 0 0 0 0 2z"/>
        </svg>
        """;
}

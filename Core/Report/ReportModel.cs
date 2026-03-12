namespace MemorySnapshotDataTools.Report;

/// <summary>Root model for the HTML report: title, db path, generated timestamp, and ordered groups with nav.</summary>
internal sealed class ReportModel
{
    /// <summary>Report title (e.g. "Memory Snapshot Report").</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Database path shown in the subtitle.</summary>
    public string DbPath { get; set; } = string.Empty;

    /// <summary>When the report was generated (UTC string).</summary>
    public string GeneratedAtUtc { get; set; } = string.Empty;

    /// <summary>Content groups (Snapshot Info, Native Objects, Managed Heap, etc.).</summary>
    public List<ReportGroup> Groups { get; } = [];

    /// <summary>Navigation groups for the sidebar (mirrors group/section structure).</summary>
    public List<NavGroup> NavGroups { get; } = [];
}

/// <summary>Logical group of sections (e.g. "Native Objects") with a title and optional description.</summary>
internal sealed class ReportGroup
{
    /// <summary>Group heading (e.g. "Native Objects").</summary>
    public string GroupTitle { get; set; } = string.Empty;

    /// <summary>Optional short description.</summary>
    public string GroupDesc { get; set; } = string.Empty;

    /// <summary>Sections within this group.</summary>
    public List<ReportSection> Sections { get; } = [];
}

/// <summary>Single report section: anchor id, title, HTML content, and optional row count badge.</summary>
internal sealed class ReportSection
{
    /// <summary>Fragment id for nav links (e.g. "native-overview").</summary>
    public string Anchor { get; set; } = string.Empty;

    /// <summary>Section heading.</summary>
    public string SectionTitle { get; set; } = string.Empty;

    /// <summary>Rendered HTML for the section body.</summary>
    public string ContentHtml { get; set; } = string.Empty;

    /// <summary>Optional row count for badge display.</summary>
    public int? RowCount { get; set; }
}

/// <summary>Single navigation link (anchor + display title).</summary>
internal sealed class NavItem
{
    /// <summary>Fragment id matching a section anchor.</summary>
    public string Anchor { get; set; } = string.Empty;

    /// <summary>Link text.</summary>
    public string Title { get; set; } = string.Empty;
}

/// <summary>Navigation group: label and list of links.</summary>
internal sealed class NavGroup
{
    /// <summary>Group label in the nav (e.g. "Native Objects").</summary>
    public string GroupTitle { get; set; } = string.Empty;

    /// <summary>Links in this nav group.</summary>
    public List<NavItem> Items { get; } = [];
}

namespace AICollaborationPlatform.App.Models;

// Read models for browsing the local copy of Jira data. Dates are already converted to local time for display.

public sealed record ProjectRow(string Key, string Name, int IssueCount);

/// <summary>One row of the issue table: just what the table shows, so 10,000+ rows stay light.</summary>
public sealed record IssueRow(
    string Id,
    string Key,
    string? ProjectKey,
    string Summary,
    string? Type,
    string? Status,
    string? StatusCategory,
    string? Assignee,
    string? Priority,
    DateTime? Created,
    DateTime? Updated)
{
    /// <summary>The number after the dash, so KAN-9 sorts before KAN-10.</summary>
    public int KeyNumber { get; } = int.TryParse(Key.AsSpan(Key.LastIndexOf('-') + 1), out var n) ? n : 0;
}

public sealed record IssueDetail(
    string Id,
    string Key,
    string Summary,
    string? Type,
    string? Status,
    string? StatusCategory,
    string? Priority,
    string? Assignee,
    string? Reporter,
    DateTime? Created,
    DateTime? Updated,
    DateTime? Resolved,
    IReadOnlyList<string> Labels,
    string? ParentKey,
    string? Description,
    IReadOnlyList<CommentRow> Comments,
    IReadOnlyList<ChangeRow> Changes,
    IReadOnlyList<AttachmentRow> Attachments)
{
    public string LabelsText => Labels.Count == 0 ? "None" : string.Join(", ", Labels);
}

public sealed record CommentRow(string? Author, DateTime? Created, string Body);

public sealed record ChangeRow(DateTime? Created, string? Author, string Field, string? From, string? To);

public sealed record AttachmentRow(string Filename, long Size, string? Author, DateTime? Created, string? ContentUrl)
{
    public string SizeText => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0.#} KB",
        _ => $"{Size / (1024.0 * 1024):0.#} MB",
    };
}

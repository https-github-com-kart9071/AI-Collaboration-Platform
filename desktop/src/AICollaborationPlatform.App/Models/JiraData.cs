using System.Text.Json;

namespace AICollaborationPlatform.App.Models;

// Jira data returned by IJiraClient. Shapes are the same for Cloud and Server;
// the client smooths over their differences (account IDs vs user keys, rich-text vs plain descriptions).

/// <summary>A Jira user. <see cref="Id"/> is the Cloud accountId, or the Server user key.</summary>
public sealed record JiraPerson(string Id, string DisplayName, string? EmailAddress, bool Active);

public sealed record JiraProject(string Id, string Key, string Name, string? ProjectTypeKey, JiraPerson? Lead);

/// <summary>
/// An issue with its common fields typed, plus every field as raw JSON in <see cref="Fields"/>
/// (custom fields included) so nothing is lost when storing it.
/// </summary>
public sealed record JiraIssue(
    string Id,
    string Key,
    string? ProjectKey,
    string Summary,
    string? IssueType,
    string? Status,
    string? StatusCategory,
    string? Priority,
    JiraPerson? Assignee,
    JiraPerson? Reporter,
    DateTimeOffset? Created,
    DateTimeOffset? Updated,
    DateTimeOffset? ResolutionDate,
    string? Description,
    IReadOnlyList<string> Labels,
    string? ParentKey,
    JsonElement Fields,
    IReadOnlyList<JiraComment> Comments,
    int CommentTotal,
    IReadOnlyList<JiraAttachment> Attachments,
    IReadOnlyList<JiraChange> Changes)
{
    /// <summary>Jira includes only the first comments inline; true when the rest must be fetched separately.</summary>
    public bool HasMoreComments => CommentTotal > Comments.Count;
}

/// <summary>Attachment details only; the file itself isn't downloaded.</summary>
public sealed record JiraAttachment(string Id, string Filename, long Size, string? MimeType, DateTimeOffset? Created, JiraPerson? Author, string? ContentUrl);

/// <summary>One field change from an issue's history (changelog).</summary>
public sealed record JiraChange(string HistoryId, JiraPerson? Author, DateTimeOffset? Created, string Field, string? From, string? To);

public sealed record JiraComment(string Id, JiraPerson? Author, string Body, DateTimeOffset? Created, DateTimeOffset? Updated);

public sealed record JiraWorklog(string Id, JiraPerson? Author, long TimeSpentSeconds, DateTimeOffset? Started, string? Comment);

public sealed record JiraBoard(long Id, string Name, string? Type, string? ProjectKey);

public sealed record JiraSprint(
    long Id,
    string Name,
    string? State,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate,
    DateTimeOffset? CompleteDate,
    string? Goal,
    long? BoardId);

public sealed record JiraField(string Id, string Name, bool Custom, string? SchemaType);

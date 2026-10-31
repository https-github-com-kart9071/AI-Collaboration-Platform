using AICollaborationPlatform.App.Models;

namespace AICollaborationPlatform.App.Services;

/// <summary>
/// Reads data from one Jira connection. Lists are streamed page by page as <see cref="IAsyncEnumerable{T}"/>,
/// so callers can show progress and stop early: <c>await foreach (var issue in client.SearchIssuesAsync(jql))</c>.
/// Errors throw <see cref="JiraApiException"/>; temporary failures are retried first.
/// </summary>
public interface IJiraClient
{
    IAsyncEnumerable<JiraProject> GetProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds issues with JQL, e.g. <c>project = ABC ORDER BY updated</c>. Returns all fields unless
    /// <paramref name="fields"/> narrows them. Jira Cloud rejects unbounded JQL, so include a restriction such as a project.
    /// Set <paramref name="includeChangelog"/> to get each issue's recent history in <see cref="JiraIssue.Changes"/>.
    /// </summary>
    IAsyncEnumerable<JiraIssue> SearchIssuesAsync(
        string jql, IEnumerable<string>? fields = null, bool includeChangelog = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Just the IDs of the issues matching <paramref name="jql"/>: a quick way to see which issues still exist
    /// (Jira Cloud returns up to 5,000 per page when only IDs are asked for).
    /// </summary>
    IAsyncEnumerable<string> GetIssueIdsAsync(string jql, CancellationToken cancellationToken = default);

    /// <summary>How many issues match <paramref name="jql"/>. Approximate on Jira Cloud.</summary>
    Task<long> CountIssuesAsync(string jql, CancellationToken cancellationToken = default);

    /// <summary>One issue with all fields and its recent history.</summary>
    Task<JiraIssue> GetIssueAsync(string issueKey, CancellationToken cancellationToken = default);

    IAsyncEnumerable<JiraComment> GetCommentsAsync(string issueKey, CancellationToken cancellationToken = default);

    IAsyncEnumerable<JiraWorklog> GetWorklogsAsync(string issueKey, CancellationToken cancellationToken = default);

    IAsyncEnumerable<JiraPerson> GetUsersAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<JiraBoard> GetBoardsAsync(string? projectKey = null, CancellationToken cancellationToken = default);

    /// <summary>Sprints of a Scrum board. Kanban boards have none and return an empty list.</summary>
    IAsyncEnumerable<JiraSprint> GetSprintsAsync(long boardId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JiraField>> GetFieldsAsync(CancellationToken cancellationToken = default);

    // ---- Changes. These are never retried automatically, so a comment can't be posted twice. ----

    /// <summary>Adds a plain-text comment. Line breaks are kept.</summary>
    Task<JiraComment> AddCommentAsync(string issueKey, string body, CancellationToken cancellationToken = default);

    /// <summary>The status changes the workflow allows from where the issue is now.</summary>
    Task<IReadOnlyList<JiraTransition>> GetTransitionsAsync(string issueKey, CancellationToken cancellationToken = default);

    Task TransitionIssueAsync(string issueKey, string transitionId, CancellationToken cancellationToken = default);

    Task UpdateIssueAsync(string issueKey, JiraIssueUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Assigns the issue; null unassigns it. Use an id from <see cref="GetAssignableUsersAsync"/>.</summary>
    Task AssignIssueAsync(string issueKey, string? assigneeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// People who can be assigned the issue, matching <paramref name="query"/> (name or email; empty for the first 50).
    /// Their <see cref="JiraPerson.Id"/> is what <see cref="AssignIssueAsync"/> expects: the accountId on Cloud, the username on Server.
    /// </summary>
    Task<IReadOnlyList<JiraPerson>> GetAssignableUsersAsync(string issueKey, string? query = null, CancellationToken cancellationToken = default);

    /// <summary>Priority names, highest first.</summary>
    Task<IReadOnlyList<string>> GetPrioritiesAsync(CancellationToken cancellationToken = default);
}

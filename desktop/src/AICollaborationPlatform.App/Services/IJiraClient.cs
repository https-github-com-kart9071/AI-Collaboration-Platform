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
    /// </summary>
    IAsyncEnumerable<JiraIssue> SearchIssuesAsync(string jql, IEnumerable<string>? fields = null, CancellationToken cancellationToken = default);

    Task<JiraIssue> GetIssueAsync(string issueKey, CancellationToken cancellationToken = default);

    IAsyncEnumerable<JiraComment> GetCommentsAsync(string issueKey, CancellationToken cancellationToken = default);

    IAsyncEnumerable<JiraWorklog> GetWorklogsAsync(string issueKey, CancellationToken cancellationToken = default);

    IAsyncEnumerable<JiraPerson> GetUsersAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<JiraBoard> GetBoardsAsync(string? projectKey = null, CancellationToken cancellationToken = default);

    /// <summary>Sprints of a Scrum board. Kanban boards have none and return an empty list.</summary>
    IAsyncEnumerable<JiraSprint> GetSprintsAsync(long boardId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JiraField>> GetFieldsAsync(CancellationToken cancellationToken = default);
}

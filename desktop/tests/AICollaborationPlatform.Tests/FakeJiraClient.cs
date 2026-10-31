using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.Storage;

namespace AICollaborationPlatform.Tests;

/// <summary>An in-memory Jira for sync tests. Fill the collections, then read what was asked for.</summary>
internal sealed partial class FakeJiraClient : IJiraClient
{
    public List<JiraField> Fields { get; } = [new("summary", "Summary", false, "string")];

    public List<JiraProject> Projects { get; } = [];

    public List<JiraPerson> Users { get; } = [];

    public Exception? UsersError { get; set; }

    public Dictionary<string, List<JiraIssue>> IssuesByProject { get; } = [];

    public Dictionary<string, List<JiraComment>> AllComments { get; } = [];

    public Dictionary<string, List<JiraBoard>> BoardsByProject { get; } = [];

    public Dictionary<long, List<JiraSprint>> SprintsByBoard { get; } = [];

    public Exception? BoardsError { get; set; }

    public Exception? SearchError { get; set; }

    /// <summary>Called before each issue is returned; tests use it to cancel part-way.</summary>
    public Action<JiraIssue>? BeforeIssue { get; set; }

    public List<string> SearchedJql { get; } = [];

    public List<bool> SearchedWithChangelog { get; } = [];

    public List<string> CommentRequests { get; } = [];

    public IAsyncEnumerable<JiraProject> GetProjectsAsync(CancellationToken cancellationToken = default) => Yield(Projects, cancellationToken);

    public async IAsyncEnumerable<JiraIssue> SearchIssuesAsync(
        string jql, IEnumerable<string>? fields = null, bool includeChangelog = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SearchedJql.Add(jql);
        SearchedWithChangelog.Add(includeChangelog);
        if (SearchError is not null) throw SearchError;

        foreach (var issue in IssuesByProject.GetValueOrDefault(ProjectOf(jql)) ?? [])
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            BeforeIssue?.Invoke(issue);
            cancellationToken.ThrowIfCancellationRequested();
            yield return issue;
        }
    }

    public Task<long> CountIssuesAsync(string jql, CancellationToken cancellationToken = default) =>
        Task.FromResult((long)(IssuesByProject.GetValueOrDefault(ProjectOf(jql))?.Count ?? 0));

    public Task<JiraIssue> GetIssueAsync(string issueKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(IssuesByProject.Values.SelectMany(i => i).First(i => i.Key == issueKey));

    public IAsyncEnumerable<JiraComment> GetCommentsAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        CommentRequests.Add(issueKey);
        return Yield(AllComments.GetValueOrDefault(issueKey) ?? [], cancellationToken);
    }

    public IAsyncEnumerable<JiraWorklog> GetWorklogsAsync(string issueKey, CancellationToken cancellationToken = default) =>
        Yield(new List<JiraWorklog>(), cancellationToken);

    public async IAsyncEnumerable<JiraPerson> GetUsersAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var user in Users)
        {
            await Task.Yield();
            yield return user;
        }

        if (UsersError is not null) throw UsersError;
    }

    public IAsyncEnumerable<JiraBoard> GetBoardsAsync(string? projectKey = null, CancellationToken cancellationToken = default) =>
        BoardsError is not null ? Throwing<JiraBoard>(BoardsError) : Yield(BoardsByProject.GetValueOrDefault(projectKey ?? "") ?? [], cancellationToken);

    public IAsyncEnumerable<JiraSprint> GetSprintsAsync(long boardId, CancellationToken cancellationToken = default) =>
        Yield(SprintsByBoard.GetValueOrDefault(boardId) ?? [], cancellationToken);

    public Task<IReadOnlyList<JiraField>> GetFieldsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<JiraField>>(Fields);

    private static string ProjectOf(string jql) => ProjectInJql().Match(jql).Groups[1].Value;

    private static async IAsyncEnumerable<T> Yield<T>(IEnumerable<T> items, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in items.ToList())
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

#pragma warning disable CS1998 // async iterator that only throws
    private static async IAsyncEnumerable<T> Throwing<T>(Exception ex)
    {
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
#pragma warning restore CS1998

    [GeneratedRegex("project = \"([^\"]+)\"")]
    private static partial Regex ProjectInJql();
}

internal sealed class FakeJiraClientFactory(IJiraClient client) : IJiraClientFactory
{
    public IJiraClient Create(JiraConnectionSettings settings) => client;
}

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Quick builders for Jira test data.</summary>
internal static class TestData
{
    public static readonly JiraPerson Alex = new("acc-alex", "Alex Kim", "alex@acme.com", true);
    public static readonly JiraPerson Riya = new("acc-riya", "Riya Shah", null, true);

    public static JiraProject Project(string key, string? id = null) => new(id ?? $"p-{key}", key, $"{key} project", "software", Alex);

    public static JiraIssue Issue(
        string id, string key, string project,
        IReadOnlyList<JiraComment>? comments = null, int? commentTotal = null,
        IReadOnlyList<JiraAttachment>? attachments = null, IReadOnlyList<JiraChange>? changes = null,
        string summary = "An issue", JiraPerson? assignee = null) =>
        new(id, key, project, summary, "Task", "To Do", "new", "Medium", assignee ?? Alex, Riya,
            new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 2, 1, 10, 0, 0, TimeSpan.Zero), null,
            "Description text", ["label1"], null, JsonDocument.Parse("""{"summary":"An issue","customfield_1":7}""").RootElement.Clone(),
            comments ?? [], commentTotal ?? comments?.Count ?? 0, attachments ?? [], changes ?? []);

    public static JiraComment Comment(string id, string body = "A comment", JiraPerson? author = null) =>
        new(id, author ?? Riya, body, new DateTimeOffset(2024, 1, 2, 9, 0, 0, TimeSpan.Zero), null);

    public static JiraDatabaseFactory TempDatabase() =>
        new(Path.Combine(Path.GetTempPath(), "AICollabTests", Guid.NewGuid().ToString("N"), "data.db"));

    public static JiraApiException Forbidden() => new(HttpStatusCode.Forbidden, "This account isn't allowed to read that in Jira.", []);
}

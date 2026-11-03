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

    /// <summary>
    /// Issues that exist in Jira, per project, when that differs from what searches return
    /// (tests empty <see cref="IssuesByProject"/> to mean "nothing changed", not "everything was deleted").
    /// </summary>
    public Dictionary<string, List<string>> ExistingIssueIds { get; } = [];

    public List<string> IdQueries { get; } = [];

    public Exception? IdsError { get; set; }

    public IAsyncEnumerable<string> GetIssueIdsAsync(string jql, CancellationToken cancellationToken = default)
    {
        IdQueries.Add(jql);
        if (IdsError is not null) return Throwing<string>(IdsError);
        var project = ProjectOf(jql);
        return Yield(ExistingIssueIds.GetValueOrDefault(project)
            ?? (IssuesByProject.GetValueOrDefault(project) ?? []).Select(i => i.Id).ToList(), cancellationToken);
    }

    public Task<long> CountIssuesAsync(string jql, CancellationToken cancellationToken = default) =>
        Task.FromResult((long)(IssuesByProject.GetValueOrDefault(ProjectOf(jql))?.Count ?? 0));

    public Task<JiraIssue> GetIssueAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        Fail("get");
        return Task.FromResult(IssuesByProject.Values.SelectMany(i => i).First(i => i.Key == issueKey));
    }

    // ---- Changes: applied to the in-memory issues, so reading the issue again shows them ----

    /// <summary>Every change made, in order, e.g. "assign ABC-1 acc-riya".</summary>
    public List<string> Changes { get; } = [];

    /// <summary>Makes a call fail: "get", "comment", "transition", "update", "assign", "transitions", "assignable", "priorities".</summary>
    public Dictionary<string, Exception> FailOn { get; } = [];

    public List<JiraTransition> Transitions { get; } = [];

    public List<string> Priorities { get; } = ["Highest", "High", "Medium", "Low", "Lowest"];

    public List<string> AssignableQueries { get; } = [];

    public Task<JiraComment> AddCommentAsync(string issueKey, string body, CancellationToken cancellationToken = default)
    {
        Fail("comment");
        var comment = new JiraComment($"new-{Changes.Count}", TestData.Alex, body, DateTimeOffset.UtcNow, null);
        Change(issueKey, $"comment {issueKey} {body}", i => i with { Comments = [.. i.Comments, comment], CommentTotal = i.CommentTotal + 1 });
        return Task.FromResult(comment);
    }

    public Task<IReadOnlyList<JiraTransition>> GetTransitionsAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        Fail("transitions");
        return Task.FromResult<IReadOnlyList<JiraTransition>>(Transitions);
    }

    public Task TransitionIssueAsync(string issueKey, string transitionId, CancellationToken cancellationToken = default)
    {
        Fail("transition");
        var to = Transitions.First(t => t.Id == transitionId);
        Change(issueKey, $"transition {issueKey} {transitionId}", i => i with { Status = to.ToStatus, StatusCategory = to.ToStatusCategory });
        return Task.CompletedTask;
    }

    public Task UpdateIssueAsync(string issueKey, JiraIssueUpdate update, CancellationToken cancellationToken = default)
    {
        Fail("update");
        Change(issueKey, $"update {issueKey} {update}", i => i with
        {
            Summary = update.Summary ?? i.Summary,
            Description = update.Description is null ? i.Description : update.Description.Length == 0 ? null : update.Description,
            Priority = update.Priority ?? i.Priority,
        });
        return Task.CompletedTask;
    }

    public Task AssignIssueAsync(string issueKey, string? assigneeId, CancellationToken cancellationToken = default)
    {
        Fail("assign");
        Change(issueKey, $"assign {issueKey} {assigneeId ?? "nobody"}", i => i with { Assignee = Users.FirstOrDefault(u => u.Id == assigneeId) });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JiraPerson>> GetAssignableUsersAsync(string issueKey, string? query = null, CancellationToken cancellationToken = default)
    {
        Fail("assignable");
        AssignableQueries.Add(query ?? "");
        return Task.FromResult<IReadOnlyList<JiraPerson>>(
            Users.Where(u => string.IsNullOrEmpty(query) || u.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    public Task<IReadOnlyList<string>> GetPrioritiesAsync(CancellationToken cancellationToken = default)
    {
        Fail("priorities");
        return Task.FromResult<IReadOnlyList<string>>(Priorities);
    }

    private void Fail(string call)
    {
        if (FailOn.TryGetValue(call, out var error)) throw error;
    }

    /// <summary>Replaces the issue with its changed copy and bumps its Updated time, like Jira does.</summary>
    private void Change(string issueKey, string description, Func<JiraIssue, JiraIssue> change)
    {
        var list = IssuesByProject.Values.First(l => l.Any(i => i.Key == issueKey));
        var index = list.FindIndex(i => i.Key == issueKey);
        list[index] = change(list[index]) with { Updated = DateTimeOffset.UtcNow };
        Changes.Add(description);
    }

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

    /// <summary>When set, the first call (fields) waits for it, so a test can hold a fetch "running".</summary>
    public TaskCompletionSource? HoldFields { get; set; }

    public async Task<IReadOnlyList<JiraField>> GetFieldsAsync(CancellationToken cancellationToken = default)
    {
        if (HoldFields is { } hold) await hold.Task;
        return Fields;
    }

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

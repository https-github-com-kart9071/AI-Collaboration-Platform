using System.IO;
using System.Net;
using System.Net.Http;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Storage;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.App.Services;

public interface IJiraSyncService
{
    /// <summary>Projects on the connected Jira site, for choosing what to fetch.</summary>
    Task<IReadOnlyList<JiraProject>> GetProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches Jira data into the local database. Never throws for Jira or cancellation problems;
    /// the result says how it ended. Safe to cancel at any point: what was saved stays consistent.
    /// </summary>
    Task<JiraSyncResult> SyncAsync(JiraSyncOptions options, IProgress<JiraSyncProgress>? progress = null, CancellationToken cancellationToken = default);

    JiraDataSummary GetSummary();
}

/// <summary>
/// Copies Jira into the local SQLite database: fields, projects and users, then for each project
/// its issues (with comments, attachments and history), boards and sprints.
/// <para>
/// Re-fetching is incremental: each project remembers when it was last fully fetched, and only issues
/// updated since then are requested. The marker moves only after a project's issues are all saved,
/// so a cancelled or failed project is simply fetched again next time.
/// </para>
/// </summary>
public sealed class JiraSyncService(
    IJiraSession session,
    IJiraClientFactory clientFactory,
    JiraDatabaseFactory databaseFactory,
    TimeProvider time,
    ILogger<JiraSyncService> logger) : IJiraSyncService
{
    private const int BatchSize = 50;

    /// <summary>Extra overlap for incremental fetches, so nothing slips between runs.</summary>
    private static readonly TimeSpan IncrementalOverlap = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<JiraProject>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
        await CollectAsync(CreateClient().GetProjectsAsync(cancellationToken));

    public JiraDataSummary GetSummary()
    {
        // Don't create an empty database just to report that it's empty.
        if (!File.Exists(databaseFactory.Path))
        {
            return new JiraDataSummary(0, 0, 0, 0, 0, 0, null, null);
        }

        using var db = databaseFactory.Open();
        return db.GetSummary();
    }

    public async Task<JiraSyncResult> SyncAsync(
        JiraSyncOptions options, IProgress<JiraSyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var site = session.Settings!.BaseUrl.AbsoluteUri.TrimEnd('/');
        var filter = string.IsNullOrWhiteSpace(options.JqlFilter) ? null : options.JqlFilter.Trim();
        var startedAt = time.GetUtcNow();
        var startTimestamp = time.GetTimestamp();
        var run = new RunCounters();

        using var db = databaseFactory.Open();

        if (db.GetSite() is { } previousSite && !string.Equals(previousSite, site, StringComparison.OrdinalIgnoreCase))
        {
            // Never mix two Jira sites in one database.
            db.ClearAllData();
            run.Warnings.Add($"Data from {previousSite} was removed because you're now connected to {site}.");
        }

        db.SetSite(site);
        var runId = db.StartSyncRun(startedAt, filter);
        logger.LogInformation("Jira fetch started for {Site} (projects: {Projects}, filter: {Filter}, full refresh: {Full})",
            site, options.ProjectKeys is { Count: > 0 } k ? string.Join(",", k) : "all", filter ?? "none", options.FullRefresh);

        JiraSyncStatus status;
        string? error = null;
        try
        {
            progress?.Report(new JiraSyncProgress(JiraSyncPhase.Fields, 0, 0, null, 0, null));
            var fields = await client.GetFieldsAsync(cancellationToken);
            db.InTransaction(() => fields.ToList().ForEach(db.UpsertField));

            progress?.Report(new JiraSyncProgress(JiraSyncPhase.Projects, 0, 0, null, 0, null));
            var allProjects = await CollectAsync(client.GetProjectsAsync(cancellationToken));
            db.InTransaction(() => allProjects.ForEach(p => db.UpsertProject(p, startedAt)));
            var projects = SelectProjects(allProjects, options.ProjectKeys, run.Warnings);

            progress?.Report(new JiraSyncProgress(JiraSyncPhase.Users, 0, projects.Count, null, 0, null));
            await SyncUsersAsync(client, db, run, cancellationToken);

            for (var i = 0; i < projects.Count; i++)
            {
                await SyncProjectAsync(client, db, projects[i], i + 1, projects.Count, filter, options.FullRefresh, startedAt, run, progress, cancellationToken);
            }

            status = JiraSyncStatus.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = JiraSyncStatus.Cancelled;
            logger.LogInformation("Jira fetch cancelled");
        }
        catch (Exception ex)
        {
            status = JiraSyncStatus.Failed;
            error = ex is JiraApiException or HttpRequestException ? ex.Message : $"Unexpected error: {ex.Message}";
            logger.LogError(ex, "Jira fetch failed");
        }

        var duration = time.GetElapsedTime(startTimestamp);
        db.FinishSyncRun(runId, status.ToString().ToLowerInvariant(), time.GetUtcNow(), run.Projects, run.Issues, run.Comments, error);
        logger.LogInformation("Jira fetch {Status}: {Projects} projects, {Issues} issues, {Comments} comments in {Duration}",
            status, run.Projects, run.Issues, run.Comments, duration);

        return new JiraSyncResult(status, run.Projects, run.Issues, run.Comments, run.Boards, run.Sprints, run.IssuesRemoved,
            duration, error, run.Warnings);
    }

    private async Task SyncProjectAsync(
        IJiraClient client, JiraDatabase db, JiraProject project, int index, int count, string? filter, bool fullRefresh,
        DateTimeOffset startedAt, RunCounters run, IProgress<JiraSyncProgress>? progress, CancellationToken cancellationToken)
    {
        // Incremental only if the last full fetch of this project used the same filter.
        var (lastSyncedAt, lastFilter) = db.GetProjectSyncState(project.Key);
        var since = !fullRefresh && lastSyncedAt is { } last && lastFilter == filter ? last : (DateTimeOffset?)null;
        var jql = BuildJql(project.Key, filter, since, startedAt);

        long? total = null;
        try
        {
            total = await client.CountIssuesAsync(jql, cancellationToken);
        }
        catch (Exception ex) when (ex is JiraApiException or HttpRequestException)
        {
            logger.LogWarning(ex, "Couldn't count issues for {Project}; progress will show issues fetched only", project.Key);
        }

        progress?.Report(new JiraSyncProgress(JiraSyncPhase.Issues, index, count, project.Key, 0, total));

        var batch = new List<(JiraIssue Issue, IReadOnlyList<JiraComment> Comments)>(BatchSize);
        var seenIssueIds = new HashSet<string>();
        var done = 0;

        void Flush()
        {
            db.InTransaction(() => batch.ForEach(b => db.SaveIssue(b.Issue, b.Comments, startedAt)));
            run.Issues += batch.Count;
            run.Comments += batch.Sum(b => b.Comments.Count);
            batch.Clear();
        }

        await foreach (var issue in client.SearchIssuesAsync(jql, includeChangelog: true, cancellationToken: cancellationToken))
        {
            // Jira inlines only the first comments; fetch the rest when there are more.
            var comments = issue.HasMoreComments
                ? await CollectAsync(client.GetCommentsAsync(issue.Key, cancellationToken))
                : issue.Comments;

            batch.Add((issue, comments));
            seenIssueIds.Add(issue.Id);
            if (batch.Count == BatchSize)
            {
                Flush();
            }

            progress?.Report(new JiraSyncProgress(JiraSyncPhase.Issues, index, count, project.Key, ++done, total));
        }

        Flush();

        // A complete, unfiltered fetch shows exactly which issues exist, so drop ones that were deleted in Jira.
        if (since is null && filter is null)
        {
            run.IssuesRemoved += db.DeleteIssuesNotIn(project.Key, seenIssueIds);
        }

        db.MarkProjectSynced(project.Key, startedAt, filter);
        run.Projects++;

        progress?.Report(new JiraSyncProgress(JiraSyncPhase.Boards, index, count, project.Key, done, total));
        await SyncBoardsAsync(client, db, project.Key, run, cancellationToken);
    }

    private async Task SyncUsersAsync(IJiraClient client, JiraDatabase db, RunCounters run, CancellationToken cancellationToken)
    {
        var batch = new List<JiraPerson>();
        try
        {
            await foreach (var user in client.GetUsersAsync(cancellationToken))
            {
                batch.Add(user);
                if (batch.Count == 200)
                {
                    db.InTransaction(() => batch.ForEach(db.UpsertUser));
                    batch.Clear();
                }
            }
        }
        catch (JiraApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
            // Listing users needs the "Browse users" permission; people on issues are still saved.
            run.Warnings.Add("Couldn't read the full user list (no permission). People are taken from issues instead.");
            logger.LogWarning("Skipping user list: {Message}", ex.Message);
        }

        db.InTransaction(() => batch.ForEach(db.UpsertUser));
    }

    private async Task SyncBoardsAsync(IJiraClient client, JiraDatabase db, string projectKey, RunCounters run, CancellationToken cancellationToken)
    {
        // Boards are a bonus: a permission problem here shouldn't fail the whole fetch.
        try
        {
            await foreach (var board in client.GetBoardsAsync(projectKey, cancellationToken))
            {
                db.UpsertBoard(board);
                run.Boards++;

                try
                {
                    var sprints = await CollectAsync(client.GetSprintsAsync(board.Id, cancellationToken));
                    db.InTransaction(() => sprints.ForEach(db.UpsertSprint));
                    run.Sprints += sprints.Count;
                }
                catch (JiraApiException ex)
                {
                    run.Warnings.Add($"Couldn't read sprints for board \"{board.Name}\": {ex.Message}");
                }
            }
        }
        catch (JiraApiException ex)
        {
            run.Warnings.Add($"Couldn't read boards for {projectKey}: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the JQL for one project. The "since" condition is relative (<c>updated &gt;= -90m</c>) because
    /// Jira reads absolute dates in the user's own time zone, which the app doesn't know.
    /// </summary>
    public static string BuildJql(string projectKey, string? filter, DateTimeOffset? since, DateTimeOffset now)
    {
        var jql = $"project = \"{projectKey.Replace("\"", "\\\"")}\"";

        if (filter is not null)
        {
            jql += $" AND ({filter})";
        }

        if (since is { } s)
        {
            var minutes = (int)Math.Ceiling((now - s + IncrementalOverlap).TotalMinutes);
            jql += $" AND updated >= -{Math.Max(minutes, 1)}m";
        }

        return jql + " ORDER BY updated ASC";
    }

    private static List<JiraProject> SelectProjects(List<JiraProject> all, IReadOnlyList<string>? keys, List<string> warnings)
    {
        if (keys is not { Count: > 0 })
        {
            return all;
        }

        var wanted = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var selected = all.Where(p => wanted.Contains(p.Key)).ToList();
        var missing = wanted.Except(selected.Select(p => p.Key), StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            warnings.Add($"Not found in Jira (skipped): {string.Join(", ", missing)}");
        }

        return selected;
    }

    private IJiraClient CreateClient() =>
        clientFactory.Create(session.Settings ?? throw new InvalidOperationException("Connect to Jira before fetching data."));

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
    }

    private sealed class RunCounters
    {
        public int Projects;
        public int Issues;
        public int Comments;
        public int Boards;
        public int Sprints;
        public int IssuesRemoved;
        public readonly List<string> Warnings = [];
    }
}

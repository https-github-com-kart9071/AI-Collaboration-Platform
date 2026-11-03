using System.Net;
using System.Net.Http;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Storage;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.App.Services;

public enum IssueEditStatus
{
    Saved,

    /// <summary>The issue changed in Jira since the local copy was taken; nothing was sent, and the local copy is now the latest.</summary>
    Conflict,

    Failed,
}

/// <param name="Message">Why it failed, or for a saved change, a warning (e.g. the local copy couldn't be updated).</param>
public sealed record IssueEditResult(IssueEditStatus Status, string? Message = null)
{
    public bool Succeeded => Status == IssueEditStatus.Saved;
}

/// <summary>What the details editor can offer. <see cref="Error"/> says what couldn't be loaded, if anything.</summary>
public sealed record IssueEditChoices(
    IReadOnlyList<JiraTransition> Transitions, IReadOnlyList<JiraPerson> Assignees, IReadOnlyList<string> Priorities, string? Error);

/// <summary>Changes from the details editor. Null leaves a field alone; <see cref="Assign"/> with a null person unassigns.</summary>
public sealed record IssueDetailsChange(string? Priority = null, bool Assign = false, JiraPerson? Assignee = null, JiraTransition? Transition = null)
{
    public bool IsEmpty => Priority is null && !Assign && Transition is null;
}

/// <summary>
/// Makes changes to issues in Jira, then saves the issue as Jira now has it into the local database,
/// so the app shows the change straight away. Never throws for Jira or network problems; the result says what happened.
/// </summary>
public interface IJiraIssueEditor
{
    /// <summary>Why issues from <paramref name="dataSite"/> can't be changed right now, or null if they can.</summary>
    string? CannotEditReason(string? dataSite);

    /// <summary>True on Cloud, where descriptions are rich text: saving one from plain text drops its formatting.</summary>
    bool DescriptionLosesFormatting { get; }

    Task<IssueEditChoices> GetChoicesAsync(string issueKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JiraPerson>> SearchAssigneesAsync(string issueKey, string query, CancellationToken cancellationToken = default);

    Task<IssueEditResult> AddCommentAsync(string issueKey, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves summary/description changes, unless the issue was updated in Jira after <paramref name="knownUpdated"/>
    /// (the "Updated" time of the copy being edited): then nothing is sent, so nobody's newer text is overwritten.
    /// </summary>
    Task<IssueEditResult> UpdateTextAsync(string issueKey, JiraIssueUpdate update, DateTime? knownUpdated, CancellationToken cancellationToken = default);

    /// <summary>Applies priority, then assignee, then status. Stops at the first failure and says what was saved before it.</summary>
    Task<IssueEditResult> UpdateDetailsAsync(string issueKey, IssueDetailsChange change, CancellationToken cancellationToken = default);
}

public sealed class JiraIssueEditor(
    IJiraSession session,
    IJiraClientFactory clientFactory,
    JiraDatabaseFactory databaseFactory,
    TimeProvider time,
    ILogger<JiraIssueEditor> logger) : IJiraIssueEditor
{
    private const string Unreachable = "Couldn't reach Jira. Check your internet connection, then try again.";

    public bool DescriptionLosesFormatting => session.Settings?.AuthType == JiraAuthType.Cloud;

    public string? CannotEditReason(string? dataSite)
    {
        if (!session.IsConnected || session.Settings is null)
        {
            return "Connect to Jira on the Jira page to make changes.";
        }

        var site = session.Settings.BaseUrl.AbsoluteUri.TrimEnd('/');
        return dataSite is not null && !string.Equals(dataSite.TrimEnd('/'), site, StringComparison.OrdinalIgnoreCase)
            ? $"This data is from {dataSite}, but you're connected to {site}. Fetch data again to make changes."
            : null;
    }

    public async Task<IssueEditChoices> GetChoicesAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        if (Connect() is not { } client)
        {
            return new IssueEditChoices([], [], [], CannotEditReason(null));
        }

        var errors = new List<string>();
        async Task<IReadOnlyList<T>> TryLoad<T>(Func<Task<IReadOnlyList<T>>> load, string what)
        {
            try
            {
                return await load();
            }
            catch (Exception ex) when (IsJiraProblem(ex, cancellationToken))
            {
                logger.LogWarning(ex, "Couldn't load {What} for {Issue}", what, issueKey);
                errors.Add($"Couldn't load {what}: {Explain(ex)}");
                return [];
            }
        }

        // Three independent reads, done together.
        var transitions = TryLoad(() => client.GetTransitionsAsync(issueKey, cancellationToken), "statuses");
        var assignees = TryLoad(() => client.GetAssignableUsersAsync(issueKey, null, cancellationToken), "people");
        var priorities = TryLoad(() => client.GetPrioritiesAsync(cancellationToken), "priorities");
        await Task.WhenAll(transitions, assignees, priorities);

        // The same reason usually breaks all three (e.g. offline); say it once.
        return new IssueEditChoices(await transitions, await assignees, await priorities,
            errors.Count == 0 ? null : string.Join(" ", errors.Distinct()));
    }

    public async Task<IReadOnlyList<JiraPerson>> SearchAssigneesAsync(string issueKey, string query, CancellationToken cancellationToken = default)
    {
        if (Connect() is not { } client) return [];

        try
        {
            return await client.GetAssignableUsersAsync(issueKey, query, cancellationToken);
        }
        catch (Exception ex) when (IsJiraProblem(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Searching assignees for {Issue} failed", issueKey);
            return [];
        }
    }

    public async Task<IssueEditResult> AddCommentAsync(string issueKey, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Failed("Write a comment first.");
        if (CheckSite() is { } blocked) return blocked;
        var client = Connect()!;

        try
        {
            await client.AddCommentAsync(issueKey, text.Trim(), cancellationToken);
        }
        catch (Exception ex) when (IsJiraProblem(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Adding a comment to {Issue} failed", issueKey);
            return Failed($"Couldn't add the comment: {Explain(ex)}");
        }

        logger.LogInformation("Added a comment to {Issue}", issueKey);
        return new IssueEditResult(IssueEditStatus.Saved, await SaveLatestAsync(client, issueKey, cancellationToken));
    }

    public async Task<IssueEditResult> UpdateTextAsync(
        string issueKey, JiraIssueUpdate update, DateTime? knownUpdated, CancellationToken cancellationToken = default)
    {
        if (update.Summary is { } summary && string.IsNullOrWhiteSpace(summary)) return Failed("The summary can't be empty.");
        if (CheckSite() is { } blocked) return blocked;
        var client = Connect()!;

        try
        {
            var latest = await client.GetIssueAsync(issueKey, cancellationToken);
            if (ChangedSince(latest.Updated, knownUpdated))
            {
                await SaveAsync(client, latest, cancellationToken);
                logger.LogInformation("Not saving {Issue}: it changed in Jira since the local copy was taken", issueKey);
                return new IssueEditResult(IssueEditStatus.Conflict,
                    $"{issueKey} was changed in Jira since this copy was fetched, so nothing was saved. " +
                    "The latest version is shown now; check it, then save again.");
            }

            await client.UpdateIssueAsync(issueKey, update with { Summary = update.Summary?.Trim() }, cancellationToken);
        }
        catch (Exception ex) when (IsJiraProblem(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Updating {Issue} failed", issueKey);
            return Failed($"Couldn't save: {Explain(ex)}");
        }

        logger.LogInformation("Updated {Fields} of {Issue}", update.Summary is null ? "description" : "summary", issueKey);
        return new IssueEditResult(IssueEditStatus.Saved, await SaveLatestAsync(client, issueKey, cancellationToken));
    }

    public async Task<IssueEditResult> UpdateDetailsAsync(string issueKey, IssueDetailsChange change, CancellationToken cancellationToken = default)
    {
        if (change.IsEmpty) return new IssueEditResult(IssueEditStatus.Saved);
        if (CheckSite() is { } blocked) return blocked;
        var client = Connect()!;

        var saved = new List<string>();
        var step = "";
        string? error = null;
        try
        {
            if (change.Priority is { } priority)
            {
                step = "priority";
                await client.UpdateIssueAsync(issueKey, new JiraIssueUpdate(Priority: priority), cancellationToken);
                saved.Add(step);
            }

            if (change.Assign)
            {
                step = "assignee";
                await client.AssignIssueAsync(issueKey, change.Assignee?.Id, cancellationToken);
                saved.Add(step);
            }

            if (change.Transition is { } transition)
            {
                step = "status";
                await client.TransitionIssueAsync(issueKey, transition.Id, cancellationToken);
                saved.Add(step);
            }
        }
        catch (Exception ex) when (IsJiraProblem(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Changing the {Step} of {Issue} failed", step, issueKey);
            error = Explain(ex);
            if (step == "status" && ex is JiraApiException { StatusCode: HttpStatusCode.BadRequest })
            {
                // A stale move, or one whose screen asks for more fields (e.g. a resolution).
                error += " The status may have changed in Jira, or this move needs more details: open the issue in Jira to finish it.";
            }
        }

        if (saved.Count > 0) logger.LogInformation("Changed {Fields} of {Issue}", string.Join(", ", saved), issueKey);

        // Re-read the issue even after a failure (unless Jira is unreachable), so the panel shows what's really there now.
        var localWarning = error == Unreachable ? null : await SaveLatestAsync(client, issueKey, cancellationToken);

        if (error is null)
        {
            return new IssueEditResult(IssueEditStatus.Saved, localWarning);
        }

        return Failed(saved.Count == 0
            ? $"Couldn't change the {step}: {error}"
            : $"Saved the {string.Join(" and ", saved)}, but couldn't change the {step}: {error}");
    }

    // ---- Helpers ----

    private IJiraClient? Connect() =>
        session.IsConnected && session.Settings is { } settings ? clientFactory.Create(settings) : null;

    /// <summary>Refuses changes when not connected, or when the local data came from a different Jira site.</summary>
    private IssueEditResult? CheckSite()
    {
        string? dataSite = null;
        if (System.IO.File.Exists(databaseFactory.Path))
        {
            using var db = databaseFactory.Open();
            dataSite = db.GetSite();
        }

        return CannotEditReason(dataSite) is { } reason ? Failed(reason) : null;
    }

    /// <summary>Fetches the issue as Jira has it now and saves it locally. Returns a warning if that failed (the change itself still worked).</summary>
    private async Task<string?> SaveLatestAsync(IJiraClient client, string issueKey, CancellationToken cancellationToken)
    {
        try
        {
            await SaveAsync(client, await client.GetIssueAsync(issueKey, cancellationToken), cancellationToken);
            return null;
        }
        catch (Exception ex) when (IsJiraProblem(ex, cancellationToken) || ex is Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(ex, "Saved {Issue} in Jira but couldn't update the local copy", issueKey);
            return "Saved in Jira, but the app's copy couldn't be updated. Use Refresh to see the change.";
        }
    }

    private async Task SaveAsync(IJiraClient client, JiraIssue issue, CancellationToken cancellationToken)
    {
        // Jira only inlines the first comments; the database needs them all.
        var comments = issue.Comments;
        if (issue.HasMoreComments)
        {
            var all = new List<JiraComment>();
            await foreach (var comment in client.GetCommentsAsync(issue.Key, cancellationToken))
            {
                all.Add(comment);
            }

            comments = all;
        }

        using var db = databaseFactory.Open();
        db.InTransaction(() => db.SaveIssue(issue, comments, time.GetUtcNow()));
    }

    /// <summary>Allows a second of slack: Jira's times have milliseconds, and some paths round them.</summary>
    private static bool ChangedSince(DateTimeOffset? jiraUpdated, DateTime? knownUpdated) =>
        jiraUpdated is { } updated && knownUpdated is { } known && updated.LocalDateTime > known.AddSeconds(1);

    private static bool IsJiraProblem(Exception ex, CancellationToken cancellationToken) =>
        ex is JiraApiException or HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>A plain explanation of what went wrong, with what to do about it.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        JiraApiException { StatusCode: HttpStatusCode.Unauthorized } =>
            "Jira rejected the saved credentials. Reconnect on the Jira page, then try again.",
        JiraApiException { StatusCode: HttpStatusCode.Forbidden } =>
            "Your Jira account doesn't have permission to do this.",
        JiraApiException { StatusCode: HttpStatusCode.NotFound } =>
            "Jira couldn't find the issue. It may have been deleted or moved, or you can no longer see it.",
        JiraApiException { StatusCode: HttpStatusCode.BadRequest, JiraErrors.Count: > 0 } api =>
            $"Jira didn't accept it: {string.Join(" ", api.JiraErrors).TrimEnd('.')}.",
        JiraApiException api => api.Message,
        _ => Unreachable,
    };

    private static IssueEditResult Failed(string message) => new(IssueEditStatus.Failed, message);
}

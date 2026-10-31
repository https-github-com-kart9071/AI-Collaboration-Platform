namespace AICollaborationPlatform.App.Services;

/// <param name="ProjectKeys">Projects to fetch; null or empty means all.</param>
/// <param name="JqlFilter">Optional extra JQL, e.g. <c>updated &gt;= -30d</c>, combined with each project.</param>
/// <param name="FullRefresh">Re-download everything instead of only what changed since the last fetch.</param>
public sealed record JiraSyncOptions(IReadOnlyList<string>? ProjectKeys = null, string? JqlFilter = null, bool FullRefresh = false);

public enum JiraSyncPhase
{
    Starting,
    Fields,
    Projects,
    Users,
    Issues,
    Boards,
}

/// <summary>Where a fetch is up to. <see cref="IssuesTotal"/> is null when Jira couldn't say.</summary>
public sealed record JiraSyncProgress(
    JiraSyncPhase Phase, int ProjectIndex, int ProjectCount, string? ProjectKey, int IssuesDone, long? IssuesTotal);

public enum JiraSyncStatus
{
    Completed,
    Cancelled,
    Failed,
}

public sealed record JiraSyncResult(
    JiraSyncStatus Status,
    int Projects,
    int Issues,
    int Comments,
    int Boards,
    int Sprints,
    int IssuesRemoved,
    TimeSpan Duration,
    string? Error,
    IReadOnlyList<string> Warnings);

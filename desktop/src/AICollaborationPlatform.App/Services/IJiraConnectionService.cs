using AICollaborationPlatform.App.Models;

namespace AICollaborationPlatform.App.Services;

public interface IJiraConnectionService
{
    /// <summary>
    /// Calls Jira's /myself endpoint to check the URL and credentials.
    /// Never throws for connection problems; they come back as a failed result.
    /// Throws <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<JiraConnectionResult> TestConnectionAsync(JiraConnectionSettings settings, CancellationToken cancellationToken = default);
}

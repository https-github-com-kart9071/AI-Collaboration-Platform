using AICollaborationPlatform.App.Models;

namespace AICollaborationPlatform.App.Storage;

/// <summary>
/// Everything saved in settings.json. Never put secrets here.
/// </summary>
public sealed record AppSettings
{
    public SavedJiraConnection? Jira { get; init; }
}

/// <summary>The non-secret half of a Jira connection. The token lives in <see cref="ICredentialStore"/>.</summary>
public sealed record SavedJiraConnection(string BaseUrl, JiraAuthType AuthType, string? Email);

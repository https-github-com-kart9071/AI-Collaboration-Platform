using AICollaborationPlatform.App.Core;

namespace AICollaborationPlatform.App.Features.Jira;

/// <summary>The non-secret half of a Jira connection. The token lives in <see cref="ICredentialStore"/>.</summary>
public sealed record SavedJiraConnection(string BaseUrl, JiraAuthType AuthType, string? Email);

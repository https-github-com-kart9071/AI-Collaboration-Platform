namespace AICollaborationPlatform.App.Models;

/// <summary>
/// Everything needed to call a Jira instance. <see cref="Email"/> is only used for Cloud.
/// </summary>
public sealed record JiraConnectionSettings(Uri BaseUrl, JiraAuthType AuthType, string? Email, string Token)
{
    // Keep the token out of logs and debugger displays.
    public override string ToString() => $"{AuthType} {BaseUrl} ({Email ?? "no email"})";
}

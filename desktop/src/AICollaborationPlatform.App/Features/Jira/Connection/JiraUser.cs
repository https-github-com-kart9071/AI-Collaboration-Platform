namespace AICollaborationPlatform.App.Models;

/// <summary>
/// The account the credentials belong to, from Jira's /myself endpoint.
/// </summary>
public sealed record JiraUser(string DisplayName, string? EmailAddress);

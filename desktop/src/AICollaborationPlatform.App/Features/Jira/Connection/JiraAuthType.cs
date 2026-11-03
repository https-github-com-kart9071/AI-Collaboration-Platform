namespace AICollaborationPlatform.App.Models;

public enum JiraAuthType
{
    /// <summary>Jira Cloud (*.atlassian.net): email + API token, REST API v3.</summary>
    Cloud,

    /// <summary>Jira Server / Data Center: Personal Access Token, REST API v2.</summary>
    Server,
}

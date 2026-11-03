namespace AICollaborationPlatform.App.Models;

public enum JiraSessionState
{
    /// <summary>No connection saved or in use.</summary>
    Disconnected,

    /// <summary>A saved connection is being checked against Jira at startup.</summary>
    Restoring,

    Connected,

    /// <summary>A connection is saved but couldn't be verified (offline, token expired, token missing).</summary>
    NeedsAttention,
}

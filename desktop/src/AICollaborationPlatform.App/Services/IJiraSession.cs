using AICollaborationPlatform.App.Models;

namespace AICollaborationPlatform.App.Services;

/// <summary>
/// The Jira connection the app is currently using. Held in memory only;
/// saving it securely is Story 3.
/// </summary>
public interface IJiraSession
{
    JiraConnectionSettings? Settings { get; }

    JiraUser? User { get; }

    bool IsConnected { get; }

    event EventHandler? Changed;

    void Connect(JiraConnectionSettings settings, JiraUser user);

    void Disconnect();
}

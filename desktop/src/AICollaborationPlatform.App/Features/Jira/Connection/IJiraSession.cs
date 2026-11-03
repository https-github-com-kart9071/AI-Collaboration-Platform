using AICollaborationPlatform.App.Models;

namespace AICollaborationPlatform.App.Services;

/// <summary>
/// The Jira connection the app is using. A connection is saved when connecting
/// (token in Windows Credential Manager, the rest in settings.json) and restored at startup.
/// </summary>
public interface IJiraSession
{
    JiraSessionState State { get; }

    /// <summary>The connection in use, or the saved one while restoring / needing attention.</summary>
    JiraConnectionSettings? Settings { get; }

    JiraUser? User { get; }

    /// <summary>Why a saved connection needs attention, or the reconnecting message.</summary>
    string? StatusMessage { get; }

    bool IsConnected { get; }

    event EventHandler? Changed;

    /// <summary>Saves the connection, then switches to it. Throws if it couldn't be saved.</summary>
    void Connect(JiraConnectionSettings settings, JiraUser user);

    /// <summary>Disconnects and forgets the saved connection, including the token.</summary>
    void Disconnect();

    /// <summary>Loads the saved connection and checks it against Jira. Never throws.</summary>
    Task RestoreAsync(CancellationToken cancellationToken = default);
}

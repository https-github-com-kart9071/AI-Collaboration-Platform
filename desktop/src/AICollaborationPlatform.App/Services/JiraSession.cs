using AICollaborationPlatform.App.Models;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.App.Services;

public sealed class JiraSession(ILogger<JiraSession> logger) : IJiraSession
{
    public JiraConnectionSettings? Settings { get; private set; }

    public JiraUser? User { get; private set; }

    public bool IsConnected => Settings is not null;

    public event EventHandler? Changed;

    public void Connect(JiraConnectionSettings settings, JiraUser user)
    {
        Settings = settings;
        User = user;
        logger.LogInformation("Connected to Jira {Host} as {User}", settings.BaseUrl.Host, user.DisplayName);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Disconnect()
    {
        if (!IsConnected)
        {
            return;
        }

        logger.LogInformation("Disconnected from Jira {Host}", Settings!.BaseUrl.Host);
        Settings = null;
        User = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

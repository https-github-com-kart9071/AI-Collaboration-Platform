using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Storage;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.App.Services;

public sealed class JiraSession(
    IJiraConnectionService connectionService,
    ISettingsStore settingsStore,
    ICredentialStore credentialStore,
    ILogger<JiraSession> logger) : IJiraSession
{
    /// <summary>Credential Manager entry for the saved Jira token.</summary>
    public const string CredentialTarget = "AICollaborationPlatform/Jira";

    // Bumped by every Connect/Disconnect/Restore so a slow restore can't overwrite a newer change.
    private int _version;

    public JiraSessionState State { get; private set; } = JiraSessionState.Disconnected;

    public JiraConnectionSettings? Settings { get; private set; }

    public JiraUser? User { get; private set; }

    public string? StatusMessage { get; private set; }

    public bool IsConnected => State == JiraSessionState.Connected;

    public event EventHandler? Changed;

    public void Connect(JiraConnectionSettings settings, JiraUser user)
    {
        // Save first: if Credential Manager or the settings file fails, nothing changes and the caller sees why.
        credentialStore.Write(CredentialTarget, settings.Email ?? "token", settings.Token);
        settingsStore.Save(settingsStore.Load() with
        {
            Jira = new SavedJiraConnection(settings.BaseUrl.AbsoluteUri.TrimEnd('/'), settings.AuthType, settings.Email),
        });

        _version++;
        logger.LogInformation("Connected to Jira {Host} as {User}; connection saved", settings.BaseUrl.Host, user.DisplayName);
        SetState(JiraSessionState.Connected, settings, user, null);
    }

    public void Disconnect()
    {
        _version++;

        try
        {
            credentialStore.Delete(CredentialTarget);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't remove the saved Jira token from Windows Credential Manager");
        }

        try
        {
            settingsStore.Save(settingsStore.Load() with { Jira = null });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't remove the saved Jira connection from settings");
        }

        logger.LogInformation("Disconnected from Jira and forgot the saved connection");
        SetState(JiraSessionState.Disconnected, null, null, null);
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (State == JiraSessionState.Restoring)
        {
            return;
        }

        var version = ++_version;

        try
        {
            var saved = settingsStore.Load().Jira;
            if (saved is null || !Uri.TryCreate(saved.BaseUrl, UriKind.Absolute, out var baseUrl))
            {
                return;
            }

            string? token;
            try
            {
                token = credentialStore.Read(CredentialTarget);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Couldn't read the saved Jira token");
                token = null;
            }

            var settings = new JiraConnectionSettings(baseUrl, saved.AuthType, saved.Email, token ?? "");

            if (string.IsNullOrEmpty(token))
            {
                logger.LogWarning("Saved Jira connection for {Host} has no token in Credential Manager", baseUrl.Host);
                SetState(JiraSessionState.NeedsAttention, settings, null,
                    "The saved token wasn't found in Windows Credential Manager. Enter it again to reconnect.");
                return;
            }

            SetState(JiraSessionState.Restoring, settings, null, $"Reconnecting to {baseUrl.Host}…");
            var result = await connectionService.TestConnectionAsync(settings, cancellationToken);

            if (version != _version)
            {
                return; // The user connected or disconnected meanwhile.
            }

            if (result.IsSuccess)
            {
                SetState(JiraSessionState.Connected, settings, result.User, null);
            }
            else
            {
                SetState(JiraSessionState.NeedsAttention, settings, null, result.Message);
            }
        }
        catch (Exception ex) when (version == _version)
        {
            logger.LogError(ex, "Restoring the saved Jira connection failed");
            SetState(JiraSessionState.NeedsAttention, Settings, null,
                ex is OperationCanceledException ? "Reconnecting was cancelled." : "Couldn't restore the saved connection.");
        }
    }

    private void SetState(JiraSessionState state, JiraConnectionSettings? settings, JiraUser? user, string? message)
    {
        State = state;
        Settings = settings;
        User = user;
        StatusMessage = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

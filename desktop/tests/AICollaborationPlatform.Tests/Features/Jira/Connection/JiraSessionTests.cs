using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.Storage;

namespace AICollaborationPlatform.Tests;

public class JiraSessionTests
{
    private static readonly JiraUser Alex = new("Alex Kim", "alex@acme.com");

    private static readonly JiraConnectionSettings CloudSettings =
        new(new Uri("https://acme.atlassian.net"), JiraAuthType.Cloud, "alex@acme.com", "secret-token");

    private static readonly SavedJiraConnection SavedCloud =
        new("https://acme.atlassian.net", JiraAuthType.Cloud, "alex@acme.com");

    [Fact]
    public void Connect_saves_token_in_credential_store_and_the_rest_in_settings()
    {
        var settings = new InMemorySettingsStore();
        var credentials = new InMemoryCredentialStore();
        var session = TestSession.Create(settingsStore: settings, credentialStore: credentials);

        session.Connect(CloudSettings, Alex);

        Assert.Equal(JiraSessionState.Connected, session.State);
        Assert.Equal(("alex@acme.com", "secret-token"), credentials.Entries[JiraSession.CredentialTarget]);
        Assert.Equal(SavedCloud, settings.Settings.Jira);
    }

    [Fact]
    public void Connect_does_nothing_if_the_token_cannot_be_saved()
    {
        var credentials = new InMemoryCredentialStore { FailWith = new InvalidOperationException("Credential Manager is unavailable") };
        var settings = new InMemorySettingsStore();
        var session = TestSession.Create(settingsStore: settings, credentialStore: credentials);

        Assert.Throws<InvalidOperationException>(() => session.Connect(CloudSettings, Alex));

        Assert.Equal(JiraSessionState.Disconnected, session.State);
        Assert.Null(settings.Settings.Jira);
    }

    [Fact]
    public void Disconnect_forgets_token_and_settings()
    {
        var settings = new InMemorySettingsStore();
        var credentials = new InMemoryCredentialStore();
        var session = TestSession.Create(settingsStore: settings, credentialStore: credentials);
        session.Connect(CloudSettings, Alex);

        session.Disconnect();

        Assert.Equal(JiraSessionState.Disconnected, session.State);
        Assert.Null(session.Settings);
        Assert.Empty(credentials.Entries);
        Assert.Null(settings.Settings.Jira);
    }

    [Fact]
    public void Disconnect_still_disconnects_if_credential_store_fails()
    {
        var credentials = new InMemoryCredentialStore();
        var session = TestSession.Create(credentialStore: credentials);
        session.Connect(CloudSettings, Alex);
        credentials.FailWith = new InvalidOperationException("boom");

        session.Disconnect();

        Assert.Equal(JiraSessionState.Disconnected, session.State);
    }

    [Fact]
    public async Task Restore_with_nothing_saved_stays_disconnected_without_calling_jira()
    {
        var service = FakeJiraConnectionService.Returning(JiraConnectionResult.Success(Alex));
        var session = TestSession.Create(service);

        await session.RestoreAsync();

        Assert.Equal(JiraSessionState.Disconnected, session.State);
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task Restore_reconnects_with_saved_settings_and_token()
    {
        var service = FakeJiraConnectionService.Returning(JiraConnectionResult.Success(Alex));
        var settings = new InMemorySettingsStore { Settings = new AppSettings { Jira = SavedCloud } };
        var credentials = new InMemoryCredentialStore();
        credentials.Write(JiraSession.CredentialTarget, "alex@acme.com", "secret-token");
        var session = TestSession.Create(service, settings, credentials);
        var states = new List<JiraSessionState>();
        session.Changed += (_, _) => states.Add(session.State);

        await session.RestoreAsync();

        Assert.Equal([JiraSessionState.Restoring, JiraSessionState.Connected], states);
        Assert.Equal("Alex Kim", session.User!.DisplayName);
        Assert.Equal("secret-token", service.LastSettings!.Token);
        Assert.Equal(new Uri("https://acme.atlassian.net"), service.LastSettings.BaseUrl);
    }

    [Fact]
    public async Task Restore_without_token_needs_attention_and_keeps_settings_for_the_dialog()
    {
        var service = FakeJiraConnectionService.Returning(JiraConnectionResult.Success(Alex));
        var settings = new InMemorySettingsStore { Settings = new AppSettings { Jira = SavedCloud } };
        var session = TestSession.Create(service, settings);

        await session.RestoreAsync();

        Assert.Equal(JiraSessionState.NeedsAttention, session.State);
        Assert.Contains("token wasn't found", session.StatusMessage);
        Assert.Equal("alex@acme.com", session.Settings!.Email);
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task Restore_with_rejected_token_needs_attention_with_jira_message()
    {
        var service = FakeJiraConnectionService.Returning(
            JiraConnectionResult.Failure(JiraConnectionError.InvalidCredentials, "Jira rejected the token."));
        var settings = new InMemorySettingsStore { Settings = new AppSettings { Jira = SavedCloud } };
        var credentials = new InMemoryCredentialStore();
        credentials.Write(JiraSession.CredentialTarget, "alex@acme.com", "expired");
        var session = TestSession.Create(service, settings, credentials);

        await session.RestoreAsync();

        Assert.Equal(JiraSessionState.NeedsAttention, session.State);
        Assert.Equal("Jira rejected the token.", session.StatusMessage);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task Restore_survives_credential_store_errors()
    {
        var settings = new InMemorySettingsStore { Settings = new AppSettings { Jira = SavedCloud } };
        var credentials = new InMemoryCredentialStore { FailWith = new InvalidOperationException("boom") };
        var session = TestSession.Create(settingsStore: settings, credentialStore: credentials);

        await session.RestoreAsync();

        Assert.Equal(JiraSessionState.NeedsAttention, session.State);
    }

    [Fact]
    public async Task Connecting_while_restoring_wins_over_the_slower_restore()
    {
        var release = new TaskCompletionSource<JiraConnectionResult>();
        var service = new FakeJiraConnectionService((_, _) => release.Task);
        var settings = new InMemorySettingsStore { Settings = new AppSettings { Jira = SavedCloud } };
        var credentials = new InMemoryCredentialStore();
        credentials.Write(JiraSession.CredentialTarget, "alex@acme.com", "old-token");
        var session = TestSession.Create(service, settings, credentials);

        var restoring = session.RestoreAsync();
        Assert.Equal(JiraSessionState.Restoring, session.State);

        var newUser = new JiraUser("New User", null);
        session.Connect(CloudSettings with { Token = "new-token" }, newUser);
        release.SetResult(JiraConnectionResult.Failure(JiraConnectionError.InvalidCredentials, "old token rejected"));
        await restoring;

        Assert.Equal(JiraSessionState.Connected, session.State);
        Assert.Same(newUser, session.User);
    }
}

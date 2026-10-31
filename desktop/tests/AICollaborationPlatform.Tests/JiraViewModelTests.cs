using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.Storage;
using AICollaborationPlatform.App.ViewModels;

namespace AICollaborationPlatform.Tests;

public class JiraViewModelTests
{
    private static readonly JiraConnectionSettings Cloud =
        new(new Uri("https://acme.atlassian.net"), JiraAuthType.Cloud, "alex@acme.com", "token");

    [Fact]
    public void Shows_not_connected_by_default()
    {
        var vm = new JiraViewModel(TestSession.Create(), new FakeDialogService());

        Assert.True(vm.IsDisconnected);
        Assert.Equal("Not connected", vm.ConnectionStatus);
        Assert.False(vm.DisconnectCommand.CanExecute(null));
        Assert.False(vm.RetryCommand.CanExecute(null));
    }

    [Fact]
    public void Follows_session_changes()
    {
        var session = TestSession.Create();
        var vm = new JiraViewModel(session, new FakeDialogService());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        session.Connect(Cloud, new JiraUser("Alex Kim", null));

        Assert.NotEmpty(changed);
        Assert.True(vm.IsConnected);
        Assert.Equal("Connected as Alex Kim", vm.ConnectionStatus);
        Assert.Equal("https://acme.atlassian.net", vm.ConnectedUrl);
        Assert.True(vm.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public void Disconnect_asks_first_and_keeps_connection_if_declined()
    {
        var session = TestSession.Create();
        session.Connect(Cloud, new JiraUser("Alex Kim", null));
        var dialogs = new FakeDialogService { ConfirmAnswer = false };
        var vm = new JiraViewModel(session, dialogs);

        vm.DisconnectCommand.Execute(null);

        Assert.Equal(1, dialogs.ConfirmCount);
        Assert.True(session.IsConnected);
    }

    [Fact]
    public void Disconnect_forgets_connection_when_confirmed()
    {
        var session = TestSession.Create();
        session.Connect(Cloud, new JiraUser("Alex Kim", null));
        var vm = new JiraViewModel(session, new FakeDialogService { ConfirmAnswer = true });

        vm.DisconnectCommand.Execute(null);

        Assert.True(vm.IsDisconnected);
    }

    [Fact]
    public async Task Needs_attention_offers_retry_which_reconnects()
    {
        var results = new Queue<JiraConnectionResult>([
            JiraConnectionResult.Failure(JiraConnectionError.HostUnreachable, "Couldn't reach acme.atlassian.net."),
            JiraConnectionResult.Success(new JiraUser("Alex Kim", null)),
        ]);
        var service = new FakeJiraConnectionService((_, _) => Task.FromResult(results.Dequeue()));
        var settings = new InMemorySettingsStore { Settings = new AppSettings { Jira = new SavedJiraConnection("https://acme.atlassian.net", JiraAuthType.Cloud, "alex@acme.com") } };
        var credentials = new InMemoryCredentialStore();
        credentials.Write(JiraSession.CredentialTarget, "alex@acme.com", "token");
        var session = TestSession.Create(service, settings, credentials);
        var vm = new JiraViewModel(session, new FakeDialogService());

        await session.RestoreAsync();
        Assert.True(vm.NeedsAttention);
        Assert.Equal("Couldn't reach acme.atlassian.net.", vm.StatusMessage);
        Assert.True(vm.RetryCommand.CanExecute(null));

        await vm.RetryCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
    }
}

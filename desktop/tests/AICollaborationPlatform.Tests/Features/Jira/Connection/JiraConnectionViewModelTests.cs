using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.ViewModels;

namespace AICollaborationPlatform.Tests;

public class JiraConnectionViewModelTests
{
    private static readonly JiraUser Alex = new("Alex Kim", "alex@acme.com");

    private static JiraConnectionViewModel CreateFilledCloud(FakeJiraConnectionService service, App.Services.IJiraSession? session = null) =>
        new(service, session ?? TestSession.Create())
        {
            Url = "https://acme.atlassian.net/browse/ABC-1",
            Email = "alex@acme.com",
            Token = "token",
        };

    [Fact]
    public void Buttons_disabled_until_required_fields_are_filled()
    {
        var vm = new JiraConnectionViewModel(FakeJiraConnectionService.Returning(JiraConnectionResult.Success(Alex)), TestSession.Create());
        Assert.False(vm.TestConnectionCommand.CanExecute(null));

        vm.Url = "https://acme.atlassian.net";
        vm.Token = "token";
        Assert.False(vm.ConnectCommand.CanExecute(null)); // Cloud also needs an email

        vm.Email = "alex@acme.com";
        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void Server_does_not_need_an_email()
    {
        var vm = new JiraConnectionViewModel(FakeJiraConnectionService.Returning(JiraConnectionResult.Success(Alex)), TestSession.Create())
        {
            Url = "https://jira.acme.com",
            Token = "pat",
            IsServer = true,
        };

        Assert.True(vm.TestConnectionCommand.CanExecute(null));
        Assert.Equal("Personal Access Token", vm.TokenLabel);
    }

    [Fact]
    public void Atlassian_url_switches_to_cloud()
    {
        var vm = new JiraConnectionViewModel(FakeJiraConnectionService.Returning(JiraConnectionResult.Success(Alex)), TestSession.Create())
        {
            IsServer = true,
        };

        vm.Url = "https://acme.atlassian.net";

        Assert.True(vm.IsCloud);
    }

    [Fact]
    public async Task Invalid_input_shows_field_errors_and_skips_the_call()
    {
        var service = FakeJiraConnectionService.Returning(JiraConnectionResult.Success(Alex));
        var vm = new JiraConnectionViewModel(service, TestSession.Create())
        {
            Url = "http://jira.acme.com",
            Email = "not-an-email",
            Token = "token",
        };

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(0, service.CallCount);
        Assert.Contains("https://", vm.UrlError);
        Assert.NotNull(vm.EmailError);
        Assert.Null(vm.TokenError);
    }

    [Fact]
    public async Task Test_connection_normalizes_url_and_shows_success()
    {
        var service = FakeJiraConnectionService.Returning(JiraConnectionResult.Success(Alex));
        var session = TestSession.Create();
        var vm = CreateFilledCloud(service, session);

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal("https://acme.atlassian.net", vm.Url);
        Assert.Equal(new Uri("https://acme.atlassian.net"), service.LastSettings!.BaseUrl);
        Assert.Equal(ConnectionStatusKind.Success, vm.StatusKind);
        Assert.Equal("Connected as Alex Kim", vm.StatusMessage);
        Assert.False(session.IsConnected); // Test only checks; Connect connects.
    }

    [Fact]
    public async Task Failed_test_shows_error_message_and_details()
    {
        var failure = JiraConnectionResult.Failure(JiraConnectionError.InvalidCredentials, "Jira rejected the token.", "HTTP 401");
        var vm = CreateFilledCloud(FakeJiraConnectionService.Returning(failure));

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionStatusKind.Error, vm.StatusKind);
        Assert.Equal("Jira rejected the token.", vm.StatusMessage);
        Assert.Equal("HTTP 401", vm.StatusDetails);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Connect_success_stores_session_and_closes()
    {
        var session = TestSession.Create();
        var vm = CreateFilledCloud(FakeJiraConnectionService.Returning(JiraConnectionResult.Success(Alex)), session);
        bool? closedWith = null;
        vm.RequestClose += (_, connected) => closedWith = connected;

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.True(closedWith);
        Assert.True(session.IsConnected);
        Assert.Equal("Alex Kim", session.User!.DisplayName);
        Assert.Equal("alex@acme.com", session.Settings!.Email);
    }

    [Fact]
    public async Task Connect_failure_keeps_dialog_open()
    {
        var session = TestSession.Create();
        var failure = JiraConnectionResult.Failure(JiraConnectionError.HostUnreachable, "Couldn't reach it.");
        var vm = CreateFilledCloud(FakeJiraConnectionService.Returning(failure), session);
        var closed = false;
        vm.RequestClose += (_, _) => closed = true;

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task Cancel_during_test_stops_it_without_closing_or_connecting()
    {
        var started = new TaskCompletionSource();
        var service = new FakeJiraConnectionService(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return JiraConnectionResult.Success(Alex);
        });
        var session = TestSession.Create();
        var vm = CreateFilledCloud(service, session);
        var closed = false;
        vm.RequestClose += (_, _) => closed = true;

        var connecting = vm.ConnectCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.IsBusy);
        Assert.False(vm.ConnectCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);
        await connecting;

        Assert.False(closed);
        Assert.False(session.IsConnected);
        Assert.False(vm.IsBusy);
        Assert.Equal("Connection test cancelled.", vm.StatusMessage);
    }

    [Fact]
    public void Cancel_when_idle_closes_without_connecting()
    {
        var vm = CreateFilledCloud(FakeJiraConnectionService.Returning(JiraConnectionResult.Success(Alex)));
        bool? closedWith = null;
        vm.RequestClose += (_, connected) => closedWith = connected;

        vm.CancelCommand.Execute(null);

        Assert.False(closedWith);
    }

    [Fact]
    public void Reopening_while_connected_prefills_the_form()
    {
        var session = TestSession.Create();
        session.Connect(new JiraConnectionSettings(new Uri("https://jira.acme.com"), JiraAuthType.Server, null, "pat"), Alex);

        var vm = new JiraConnectionViewModel(FakeJiraConnectionService.Returning(JiraConnectionResult.Success(Alex)), session);

        Assert.Equal("https://jira.acme.com", vm.Url);
        Assert.True(vm.IsServer);
        Assert.Equal("pat", vm.Token);
    }

    [Fact]
    public async Task Editing_a_field_clears_its_error_and_the_status()
    {
        var vm = CreateFilledCloud(FakeJiraConnectionService.Returning(JiraConnectionResult.Success(Alex)));
        vm.Url = "http://bad";
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.NotNull(vm.UrlError);

        vm.Url = "https://acme.atlassian.net";

        Assert.Null(vm.UrlError);
        Assert.Equal("", vm.StatusMessage);
    }
}

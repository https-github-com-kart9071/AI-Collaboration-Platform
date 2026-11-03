using AICollaborationPlatform.App.Features.Code;

namespace AICollaborationPlatform.Tests;

public class CodeViewModelTests
{
    [Fact]
    public void Starts_not_connected_and_lists_the_supported_hosts()
    {
        var page = new CodeViewModel(new FakeDialogService());

        Assert.Equal("Code", page.Title);
        Assert.Equal("Not connected", page.ConnectionStatus);
        Assert.Contains("GitHub", page.SupportedHosts);
        Assert.Contains("GitLab", page.SupportedHosts);
    }

    [Fact]
    public void Connect_opens_the_code_connection_dialog()
    {
        var dialogs = new FakeDialogService();
        var page = new CodeViewModel(dialogs);

        page.ConnectCommand.Execute(null);

        Assert.Equal(1, dialogs.CodeDialogShownCount);
        Assert.Equal(0, dialogs.JiraDialogShownCount);
    }
}

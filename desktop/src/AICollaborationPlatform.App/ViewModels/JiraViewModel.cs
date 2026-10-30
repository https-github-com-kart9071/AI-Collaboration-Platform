using CommunityToolkit.Mvvm.ComponentModel;

namespace AICollaborationPlatform.App.ViewModels;

/// <summary>
/// Landing page for the Jira integration. The connection dialog arrives in Story 2.
/// </summary>
public sealed partial class JiraViewModel : PageViewModel
{
    public override string Title => "Jira";

    [ObservableProperty]
    private string _connectionStatus = "Not connected";
}

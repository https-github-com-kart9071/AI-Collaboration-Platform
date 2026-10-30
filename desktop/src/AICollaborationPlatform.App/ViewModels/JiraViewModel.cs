using AICollaborationPlatform.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AICollaborationPlatform.App.ViewModels;

/// <summary>
/// Jira page: shows the current connection and opens the connection dialog.
/// </summary>
public sealed partial class JiraViewModel : PageViewModel
{
    private readonly IJiraSession _session;
    private readonly IDialogService _dialogs;

    public JiraViewModel(IJiraSession session, IDialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        _session.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(ConnectionStatus));
            OnPropertyChanged(nameof(ConnectedUrl));
            DisconnectCommand.NotifyCanExecuteChanged();
        };
    }

    public override string Title => "Jira";

    public bool IsConnected => _session.IsConnected;

    public string ConnectionStatus => _session.IsConnected
        ? $"Connected as {_session.User!.DisplayName}"
        : "Not connected";

    public string? ConnectedUrl => _session.Settings?.BaseUrl.AbsoluteUri.TrimEnd('/');

    [RelayCommand]
    private void Connect() => _dialogs.ShowJiraConnectionDialog();

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void Disconnect() => _session.Disconnect();
}

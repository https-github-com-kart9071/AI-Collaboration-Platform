using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AICollaborationPlatform.App.ViewModels;

/// <summary>
/// Jira page: shows the current or saved connection and lets the user connect, retry or forget it.
/// </summary>
public sealed partial class JiraViewModel : PageViewModel
{
    private readonly IJiraSession _session;
    private readonly IDialogService _dialogs;

    public JiraViewModel(IJiraSession session, IDialogService dialogs, JiraFetchViewModel fetch)
    {
        _session = session;
        _dialogs = dialogs;
        Fetch = fetch;
        _session.Changed += (_, _) =>
        {
            OnPropertyChanged(string.Empty); // every property here derives from the session
            DisconnectCommand.NotifyCanExecuteChanged();
            RetryCommand.NotifyCanExecuteChanged();
        };
    }

    public override string Title => "Jira";

    /// <summary>The "Fetch data" card, shown while connected.</summary>
    public JiraFetchViewModel Fetch { get; }

    public JiraSessionState State => _session.State;

    public bool IsDisconnected => State == JiraSessionState.Disconnected;

    public bool IsRestoring => State == JiraSessionState.Restoring;

    public bool IsConnected => State == JiraSessionState.Connected;

    public bool NeedsAttention => State == JiraSessionState.NeedsAttention;

    public bool HasSavedConnection => !IsDisconnected;

    public string ConnectionStatus => State switch
    {
        JiraSessionState.Connected => $"Connected as {_session.User!.DisplayName}",
        JiraSessionState.Restoring => "Reconnecting…",
        JiraSessionState.NeedsAttention => "Saved connection needs attention",
        _ => "Not connected",
    };

    public string? StatusMessage => _session.StatusMessage;

    public string? ConnectedUrl => _session.Settings?.BaseUrl.AbsoluteUri.TrimEnd('/');

    [RelayCommand]
    private void Connect() => _dialogs.ShowJiraConnectionDialog();

    [RelayCommand(CanExecute = nameof(NeedsAttention))]
    private Task RetryAsync() => _session.RestoreAsync();

    [RelayCommand(CanExecute = nameof(HasSavedConnection))]
    private void Disconnect()
    {
        if (_dialogs.Confirm("Disconnect from Jira",
                $"Disconnect from {ConnectedUrl}?\n\nThe saved token will be removed from this PC. You'll need it again to reconnect."))
        {
            _session.Disconnect();
        }
    }
}

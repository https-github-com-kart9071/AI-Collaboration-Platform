namespace AICollaborationPlatform.App.Services;

/// <summary>
/// Opens windows for view models, so view models stay testable without real UI.
/// </summary>
public interface IDialogService
{
    /// <summary>Shows the Jira connection dialog. Returns true if the user connected.</summary>
    bool ShowJiraConnectionDialog();

    /// <summary>Asks a yes/no question. Returns true if the user confirmed.</summary>
    bool Confirm(string title, string message);
}

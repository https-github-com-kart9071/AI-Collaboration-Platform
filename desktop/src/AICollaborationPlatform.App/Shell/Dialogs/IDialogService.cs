namespace AICollaborationPlatform.App.Shell;

/// <summary>
/// Opens windows for view models, so view models stay testable without real UI.
/// </summary>
public interface IDialogService
{
    /// <summary>Shows the Jira connection dialog. Returns true if the user connected.</summary>
    bool ShowJiraConnectionDialog();

    /// <summary>Shows the dialog for connecting GitHub, GitLab or another Git host. Returns true if the user connected.</summary>
    bool ShowCodeConnectionDialog();

    /// <summary>Asks a yes/no question. Returns true if the user confirmed.</summary>
    bool Confirm(string title, string message);
}

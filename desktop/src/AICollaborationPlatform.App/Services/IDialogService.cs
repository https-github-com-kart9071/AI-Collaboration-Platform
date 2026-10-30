namespace AICollaborationPlatform.App.Services;

/// <summary>
/// Opens windows for view models, so view models stay testable without real UI.
/// </summary>
public interface IDialogService
{
    /// <summary>Shows the Jira connection dialog. Returns true if the user connected.</summary>
    bool ShowJiraConnectionDialog();
}

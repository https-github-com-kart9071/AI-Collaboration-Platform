using AICollaborationPlatform.App.Shell;
using CommunityToolkit.Mvvm.Input;

namespace AICollaborationPlatform.App.Features.Code;

/// <summary>
/// Code page: connect GitHub, GitLab or another Git host, then browse its repositories.
/// </summary>
public sealed partial class CodeViewModel(IDialogService dialogs) : PageViewModel
{
    public override string Title => "Code";

    /// <summary>The hosts the Connect dialog offers, shown on the page so people know what's supported.</summary>
    public IReadOnlyList<string> SupportedHosts { get; } = ["GitHub", "GitLab", "Bitbucket", "Azure DevOps", "Any Git server"];

    public string ConnectionStatus => "Not connected";

    [RelayCommand]
    private void Connect() => dialogs.ShowCodeConnectionDialog();
}

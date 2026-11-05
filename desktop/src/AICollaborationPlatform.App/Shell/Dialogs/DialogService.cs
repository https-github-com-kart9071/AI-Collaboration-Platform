using System.Windows;
using AICollaborationPlatform.App.Features.Code;
using AICollaborationPlatform.App.Features.Jira;
using Microsoft.Extensions.DependencyInjection;

namespace AICollaborationPlatform.App.Shell;

public sealed class DialogService(IServiceProvider services) : IDialogService
{
    public bool ShowJiraConnectionDialog()
    {
        // A fresh view model each time, so a cancelled dialog leaves nothing behind.
        var viewModel = services.GetRequiredService<JiraConnectionViewModel>();
        var dialog = new JiraConnectionDialog(viewModel) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true;
    }

    public bool ShowCodeConnectionDialog()
    {
        var viewModel = services.GetRequiredService<CodeConnectionViewModel>();
        var dialog = new CodeConnectionDialog(viewModel) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}

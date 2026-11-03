using System.Reflection;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AICollaborationPlatform.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IJiraSession _jiraSession;
    private readonly IDialogService _dialogs;

    public MainViewModel(INavigationService navigation, IJiraSession jiraSession, IDialogService dialogs)
    {
        _navigation = navigation;
        _jiraSession = jiraSession;
        _dialogs = dialogs;
        _navigation.CurrentPageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(SelectedItem));
        };

        _navigation.NavigateTo<HomeViewModel>();
    }

    public string AppTitle => "AI Collaboration Platform";

    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public IReadOnlyList<NavigationItem> NavigationItems { get; } =
    [
        new("Home", "", typeof(HomeViewModel)),
        new("Jira", "", typeof(JiraViewModel)),
        new("Jira data", "", typeof(JiraDataViewModel)),
    ];

    public PageViewModel? CurrentPage => _navigation.CurrentPage;

    /// <summary>
    /// Two-way bound to the sidebar list, so clicking an item navigates and
    /// navigating from code highlights the matching item.
    /// </summary>
    public NavigationItem? SelectedItem
    {
        get => NavigationItems.FirstOrDefault(i => i.PageViewModelType == CurrentPage?.GetType());
        set
        {
            if (value is { IsEnabled: true })
            {
                _navigation.NavigateTo(value.PageViewModelType);
            }
        }
    }

    /// <summary>
    /// The Home page's Jira button: go to the Jira page and, if there's no connection
    /// at all, open the connection dialog straight away. A saved connection that is
    /// reconnecting or needs attention is handled on the Jira page instead.
    /// </summary>
    [RelayCommand]
    private void OpenJira()
    {
        _navigation.NavigateTo<JiraViewModel>();

        if (_jiraSession.State == JiraSessionState.Disconnected)
        {
            _dialogs.ShowJiraConnectionDialog();
        }
    }
}

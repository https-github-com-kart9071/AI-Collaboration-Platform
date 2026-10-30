using System.Reflection;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AICollaborationPlatform.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigation;

    public MainViewModel(INavigationService navigation)
    {
        _navigation = navigation;
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

    [RelayCommand]
    private void OpenJira() => _navigation.NavigateTo<JiraViewModel>();
}

using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICollaborationPlatform.Tests;

public class NavigationTests
{
    private static (MainViewModel Main, INavigationService Navigation) CreateSut()
    {
        var services = new ServiceCollection()
            .AddSingleton<HomeViewModel>()
            .AddSingleton<JiraViewModel>()
            .BuildServiceProvider();

        var navigation = new NavigationService(services, NullLogger<NavigationService>.Instance);
        return (new MainViewModel(navigation), navigation);
    }

    [Fact]
    public void Starts_on_home_page()
    {
        var (main, _) = CreateSut();

        Assert.IsType<HomeViewModel>(main.CurrentPage);
        Assert.Equal("Home", main.SelectedItem?.Title);
    }

    [Fact]
    public void Jira_button_navigates_to_jira_page()
    {
        var (main, _) = CreateSut();
        var changed = new List<string?>();
        main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        main.OpenJiraCommand.Execute(null);

        Assert.IsType<JiraViewModel>(main.CurrentPage);
        Assert.Equal("Jira", main.SelectedItem?.Title);
        Assert.Contains(nameof(MainViewModel.CurrentPage), changed);
    }

    [Fact]
    public void Selecting_sidebar_item_navigates()
    {
        var (main, _) = CreateSut();

        main.SelectedItem = main.NavigationItems.Single(i => i.Title == "Jira");

        Assert.IsType<JiraViewModel>(main.CurrentPage);
    }

    [Fact]
    public void Page_state_survives_navigation()
    {
        var (main, navigation) = CreateSut();
        main.OpenJiraCommand.Execute(null);
        var jira = (JiraViewModel)main.CurrentPage!;
        jira.ConnectionStatus = "Connected";

        navigation.NavigateTo<HomeViewModel>();
        navigation.NavigateTo<JiraViewModel>();

        Assert.Same(jira, main.CurrentPage);
    }

    [Fact]
    public void Rejects_types_that_are_not_pages()
    {
        var (_, navigation) = CreateSut();

        Assert.Throws<ArgumentException>(() => navigation.NavigateTo(typeof(string)));
    }
}

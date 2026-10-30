using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICollaborationPlatform.Tests;

public class NavigationTests
{
    private sealed record Sut(MainViewModel Main, INavigationService Navigation, IJiraSession Session, FakeDialogService Dialogs);

    private static Sut CreateSut()
    {
        var session = TestSession.Create();
        var dialogs = new FakeDialogService();
        var services = new ServiceCollection()
            .AddSingleton<IJiraSession>(session)
            .AddSingleton<IDialogService>(dialogs)
            .AddSingleton<HomeViewModel>()
            .AddSingleton<JiraViewModel>()
            .BuildServiceProvider();

        var navigation = new NavigationService(services, NullLogger<NavigationService>.Instance);
        return new Sut(new MainViewModel(navigation, session, dialogs), navigation, session, dialogs);
    }

    [Fact]
    public void Starts_on_home_page()
    {
        var sut = CreateSut();

        Assert.IsType<HomeViewModel>(sut.Main.CurrentPage);
        Assert.Equal("Home", sut.Main.SelectedItem?.Title);
    }

    [Fact]
    public void Jira_button_navigates_to_jira_page()
    {
        var sut = CreateSut();
        var changed = new List<string?>();
        sut.Main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        sut.Main.OpenJiraCommand.Execute(null);

        Assert.IsType<JiraViewModel>(sut.Main.CurrentPage);
        Assert.Equal("Jira", sut.Main.SelectedItem?.Title);
        Assert.Contains(nameof(MainViewModel.CurrentPage), changed);
    }

    [Fact]
    public void Jira_button_opens_connection_dialog_when_not_connected()
    {
        var sut = CreateSut();

        sut.Main.OpenJiraCommand.Execute(null);

        Assert.Equal(1, sut.Dialogs.JiraDialogShownCount);
    }

    [Fact]
    public void Jira_button_skips_dialog_when_already_connected()
    {
        var sut = CreateSut();
        sut.Session.Connect(
            new JiraConnectionSettings(new Uri("https://acme.atlassian.net"), JiraAuthType.Cloud, "a@b.com", "t"),
            new JiraUser("Alex", null));

        sut.Main.OpenJiraCommand.Execute(null);

        Assert.Equal(0, sut.Dialogs.JiraDialogShownCount);
    }

    [Fact]
    public void Selecting_sidebar_item_navigates_without_opening_dialog()
    {
        var sut = CreateSut();

        sut.Main.SelectedItem = sut.Main.NavigationItems.Single(i => i.Title == "Jira");

        Assert.IsType<JiraViewModel>(sut.Main.CurrentPage);
        Assert.Equal(0, sut.Dialogs.JiraDialogShownCount);
    }

    [Fact]
    public void Page_state_survives_navigation()
    {
        var sut = CreateSut();
        sut.Navigation.NavigateTo<JiraViewModel>();
        var jira = sut.Main.CurrentPage;

        sut.Navigation.NavigateTo<HomeViewModel>();
        sut.Navigation.NavigateTo<JiraViewModel>();

        Assert.Same(jira, sut.Main.CurrentPage);
    }

    [Fact]
    public void Rejects_types_that_are_not_pages()
    {
        var sut = CreateSut();

        Assert.Throws<ArgumentException>(() => sut.Navigation.NavigateTo(typeof(string)));
    }
}

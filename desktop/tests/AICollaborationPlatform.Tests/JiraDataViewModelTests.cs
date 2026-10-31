using System.Diagnostics;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICollaborationPlatform.Tests;

internal sealed class FakeDataQueries : IJiraDataQueries
{
    public JiraDataSnapshot Snapshot { get; set; } = JiraDataSnapshot.Empty;

    public Dictionary<string, IssueDetail> Details { get; } = [];

    public int LoadCount { get; private set; }

    public JiraDataSnapshot LoadSnapshot()
    {
        LoadCount++;
        return Snapshot;
    }

    public IssueDetail? LoadIssueDetail(string issueId) => Details.GetValueOrDefault(issueId);
}

internal sealed class FakeBrowser : IBrowserLauncher
{
    public List<Uri> Opened { get; } = [];

    public void Open(Uri url) => Opened.Add(url);
}

internal sealed class FakeNavigation : INavigationService
{
    public List<Type> NavigatedTo { get; } = [];

    public PageViewModel? CurrentPage => null;

    public event EventHandler? CurrentPageChanged { add { } remove { } }

    public void NavigateTo<TPage>() where TPage : PageViewModel => NavigateTo(typeof(TPage));

    public void NavigateTo(Type pageViewModelType) => NavigatedTo.Add(pageViewModelType);
}

public class JiraDataViewModelTests
{
    private static readonly DateTime Day = new(2026, 10, 1, 9, 0, 0);

    private static IssueRow Row(string key, string summary = "Summary", string? status = "To Do", string? assignee = "Alex Kim",
        string? type = "Story", string? priority = "Medium", int updatedDaysAgo = 0, string? category = "new") =>
        new($"id-{key}", key, key[..key.IndexOf('-')], summary, type, status, category, assignee, priority,
            Day.AddDays(-30), Day.AddDays(-updatedDaysAgo));

    private static readonly IssueRow[] SampleRows =
    [
        Row("KAN-1", "Login page", "Done", "Alex Kim", "Story", updatedDaysAgo: 5, category: "done"),
        Row("KAN-2", "Fix login crash", "In Progress", "Riya Shah", "Bug", "High", updatedDaysAgo: 1, category: "indeterminate"),
        Row("KAN-10", "Export to Excel", "To Do", null, "Story", updatedDaysAgo: 3),
        Row("OPS-3", "Rotate keys", "To Do", "Alex Kim", "Task", "Low", updatedDaysAgo: 2),
    ];

    private static (JiraDataViewModel Vm, FakeDataQueries Queries, FakeBrowser Browser, FakeNavigation Navigation) Create(params IssueRow[] rows)
    {
        rows = rows.Length > 0 ? rows : SampleRows;
        var queries = new FakeDataQueries
        {
            Snapshot = new JiraDataSnapshot(
                "https://acme.atlassian.net",
                rows.GroupBy(r => r.ProjectKey!).Select(g => new ProjectRow(g.Key, $"{g.Key} project", g.Count())).OrderBy(p => p.Key).ToList(),
                rows),
        };
        var browser = new FakeBrowser();
        var navigation = new FakeNavigation();
        var vm = new JiraDataViewModel(queries, browser, navigation, NullLogger<JiraDataViewModel>.Instance) { SearchDelay = TimeSpan.Zero };
        return (vm, queries, browser, navigation);
    }

    private static async Task<JiraDataViewModel> Loaded(params IssueRow[] rows)
    {
        var (vm, _, _, _) = Create(rows);
        await vm.OnNavigatedToAsync();
        return vm;
    }

    private static List<string> Keys(JiraDataViewModel vm) => vm.Issues.Select(i => i.Key).ToList();

    [Fact]
    public async Task Loads_when_shown_newest_updated_first()
    {
        var vm = await Loaded();

        Assert.True(vm.HasData);
        Assert.False(vm.IsEmpty);
        Assert.Equal(["KAN-2", "OPS-3", "KAN-10", "KAN-1"], Keys(vm));
        Assert.Equal("4 issues", vm.CountText);
        Assert.Equal("https://acme.atlassian.net", vm.Site);
    }

    [Fact]
    public async Task Empty_database_shows_the_empty_state()
    {
        var (vm, queries, _, navigation) = Create();
        queries.Snapshot = JiraDataSnapshot.Empty;

        await vm.LoadAsync();

        Assert.True(vm.IsEmpty);
        vm.GoToJiraPageCommand.Execute(null);
        Assert.Equal([typeof(JiraViewModel)], navigation.NavigatedTo);
    }

    [Fact]
    public async Task Project_list_has_all_projects_first_with_counts()
    {
        var vm = await Loaded();

        Assert.Equal(["All projects", "KAN – KAN project", "OPS – OPS project"], vm.Projects.Select(p => p.Label));
        Assert.Equal([4, 3, 1], vm.Projects.Select(p => p.IssueCount));
        Assert.Null(vm.SelectedProject!.Key);
    }

    [Fact]
    public async Task Selecting_a_project_shows_only_its_issues_and_its_filter_values()
    {
        var vm = await Loaded();

        vm.SelectedProject = vm.Projects.Single(p => p.Key == "OPS");

        Assert.Equal(["OPS-3"], Keys(vm));
        Assert.Equal(["All types", "Task (1)"], vm.TypeOptions.Select(o => o.Label));
        Assert.Equal("1 issue", vm.CountText);
    }

    [Fact]
    public async Task Filters_by_status_assignee_and_type_together()
    {
        var vm = await Loaded();

        vm.SelectedStatus = vm.StatusOptions.Single(o => o.Value == "To Do");
        Assert.Equal(["OPS-3", "KAN-10"], Keys(vm));
        Assert.Equal("Showing 2 of 4 issues", vm.CountText);

        vm.SelectedAssignee = vm.AssigneeOptions.Single(o => o.Value == "Alex Kim");
        Assert.Equal(["OPS-3"], Keys(vm));

        vm.SelectedType = vm.TypeOptions.Single(o => o.Value == "Story");
        Assert.Empty(vm.Issues);
    }

    [Fact]
    public async Task Filter_options_list_values_with_counts_and_unassigned_last()
    {
        var vm = await Loaded();

        Assert.Equal(["All statuses", "Done (1)", "In Progress (1)", "To Do (2)"], vm.StatusOptions.Select(o => o.Label));
        Assert.Equal(["All assignees", "Alex Kim (2)", "Riya Shah (1)", "Unassigned (1)"], vm.AssigneeOptions.Select(o => o.Label));
    }

    [Fact]
    public async Task Unassigned_filter_finds_issues_without_an_assignee()
    {
        var vm = await Loaded();

        vm.SelectedAssignee = vm.AssigneeOptions.Single(o => o.Value == JiraDataViewModel.UnassignedLabel);

        Assert.Equal(["KAN-10"], Keys(vm));
    }

    [Theory]
    [InlineData("login", new[] { "KAN-2", "KAN-1" })]    // summary, any case
    [InlineData("kan-1", new[] { "KAN-10", "KAN-1" })]   // key, any case
    [InlineData("  OPS  ", new[] { "OPS-3" })]          // trimmed
    [InlineData("nothing like this", new string[0])]
    public async Task Search_matches_key_and_summary(string text, string[] expected)
    {
        var vm = await Loaded();

        vm.SearchText = text;

        Assert.Equal(expected, Keys(vm));
    }

    [Fact]
    public async Task Search_waits_for_a_pause_in_typing()
    {
        var (vm, _, _, _) = Create();
        await vm.LoadAsync();
        vm.SearchDelay = TimeSpan.FromMilliseconds(80);

        vm.SearchText = "l";
        vm.SearchText = "lo";
        vm.SearchText = "login";
        Assert.Equal(4, vm.Issues.Count); // not filtered yet

        await Task.Delay(400);
        Assert.Equal(2, vm.Issues.Count);
    }

    [Fact]
    public async Task Clear_resets_every_filter_and_the_search()
    {
        var vm = await Loaded();
        vm.SelectedStatus = vm.StatusOptions[1];
        vm.SearchText = "login";

        vm.ClearFiltersCommand.Execute(null);

        Assert.Equal("", vm.SearchText);
        Assert.Null(vm.SelectedStatus!.Value);
        Assert.Equal(4, vm.Issues.Count);
    }

    [Fact]
    public async Task Sorting_by_key_is_natural_and_toggles()
    {
        var vm = await Loaded();

        vm.SortCommand.Execute("Key");
        Assert.Equal(["KAN-1", "KAN-2", "KAN-10", "OPS-3"], Keys(vm));
        Assert.False(vm.SortDescending);

        vm.SortCommand.Execute("Key");
        Assert.Equal(["OPS-3", "KAN-10", "KAN-2", "KAN-1"], Keys(vm));
    }

    [Fact]
    public async Task Sorting_text_columns_puts_empty_values_last_both_ways()
    {
        var vm = await Loaded();

        vm.SortCommand.Execute("Assignee");
        Assert.Equal(["KAN-1", "OPS-3", "KAN-2", "KAN-10"], Keys(vm)); // Alex, Alex, Riya, (none)

        vm.SortCommand.Execute("Assignee");
        Assert.Equal(["KAN-2", "KAN-1", "OPS-3", "KAN-10"], Keys(vm)); // Riya, Alex, Alex, (none)
    }

    [Fact]
    public async Task Sorting_by_a_date_starts_newest_first()
    {
        var vm = await Loaded();
        vm.SortCommand.Execute("Summary");

        vm.SortCommand.Execute("Updated");

        Assert.True(vm.SortDescending);
        Assert.Equal("KAN-2", vm.Issues[0].Key);
    }

    [Fact]
    public async Task Opening_an_issue_shows_its_details_on_the_description_tab()
    {
        var (vm, queries, _, _) = Create();
        queries.Details["id-KAN-2"] = Detail("id-KAN-2", "KAN-2", comments: [new CommentRow("Riya", Day, "On it")]);
        await vm.LoadAsync();
        vm.DetailTabIndex = 2;

        await vm.OpenIssueCommand.ExecuteAsync(vm.Issues.Single(i => i.Key == "KAN-2"));

        Assert.True(vm.IsDetailOpen);
        Assert.False(vm.IsDetailLoading);
        Assert.Equal("KAN-2", vm.Detail!.Key);
        Assert.Equal("On it", vm.Detail.Comments.Single().Body);
        Assert.Equal(0, vm.DetailTabIndex);

        vm.CloseDetailCommand.Execute(null);
        Assert.False(vm.IsDetailOpen);
        Assert.Null(vm.Detail);
    }

    [Fact]
    public async Task Open_in_jira_opens_the_issue_page_in_the_browser()
    {
        var (vm, _, browser, _) = Create();
        await vm.LoadAsync();

        vm.OpenInJiraCommand.Execute("KAN-2");

        Assert.Equal([new Uri("https://acme.atlassian.net/browse/KAN-2")], browser.Opened);
    }

    [Fact]
    public async Task Open_in_jira_is_disabled_without_a_known_site()
    {
        var (vm, queries, _, _) = Create();
        queries.Snapshot = queries.Snapshot with { Site = null };
        await vm.LoadAsync();

        Assert.False(vm.OpenInJiraCommand.CanExecute("KAN-2"));
    }

    [Fact]
    public async Task Refresh_keeps_filters_and_closes_details_of_deleted_issues()
    {
        var (vm, queries, _, _) = Create();
        queries.Details["id-KAN-2"] = Detail("id-KAN-2", "KAN-2");
        await vm.LoadAsync();
        vm.SelectedStatus = vm.StatusOptions.Single(o => o.Value == "To Do");
        await vm.OpenIssueCommand.ExecuteAsync(vm.Issues.First());
        await vm.OpenIssueCommand.ExecuteAsync(SampleRows[1]);

        queries.Snapshot = queries.Snapshot with { Issues = SampleRows.Where(r => r.Key != "KAN-2").ToList() };
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("To Do", vm.SelectedStatus!.Value);
        Assert.False(vm.IsDetailOpen);
        Assert.Equal(2, queries.LoadCount);
    }

    [Fact]
    public async Task Filtering_and_sorting_20000_issues_stays_fast()
    {
        var statuses = new[] { "To Do", "In Progress", "Done", "Blocked" };
        var people = Enumerable.Range(1, 40).Select(i => $"Person {i}").ToArray();
        var rows = Enumerable.Range(1, 20_000)
            .Select(i => Row($"BIG-{i}", $"Issue number {i} about {(i % 7 == 0 ? "login" : "reports")}",
                statuses[i % 4], i % 9 == 0 ? null : people[i % 40], i % 3 == 0 ? "Bug" : "Story", updatedDaysAgo: i % 365))
            .ToArray();
        var vm = await Loaded(rows);

        var timer = Stopwatch.StartNew();
        vm.SelectedStatus = vm.StatusOptions.Single(o => o.Value == "In Progress");
        vm.SearchText = "login";
        vm.SortCommand.Execute("Summary");
        vm.SortCommand.Execute("Key");
        vm.ClearFiltersCommand.Execute(null);
        timer.Stop();

        Assert.Equal(20_000, vm.Issues.Count);
        Assert.Equal("BIG-1", vm.Issues[0].Key);
        // Five full filter+sort passes over 20,000 rows; well under a frame budget each on a normal PC.
        Assert.True(timer.ElapsedMilliseconds < 1500, $"Took {timer.ElapsedMilliseconds} ms");
    }

    private static IssueDetail Detail(string id, string key, IReadOnlyList<CommentRow>? comments = null) =>
        new(id, key, "Summary", "Bug", "In Progress", "indeterminate", "High", "Riya Shah", "Alex Kim", Day, Day, null,
            ["auth"], null, "Steps to reproduce", comments ?? [], [], []);
}

using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.Storage;
using AICollaborationPlatform.App.ViewModels;

namespace AICollaborationPlatform.Tests;

public class JiraFetchViewModelTests
{
    private static readonly JiraConnectionSettings Acme =
        new(new Uri("https://acme.atlassian.net"), JiraAuthType.Cloud, "alex@acme.com", "t");

    private static JiraSession Connected()
    {
        var session = TestSession.Create();
        session.Connect(Acme, new JiraUser("Alex Kim", null));
        return session;
    }

    private static JiraSyncResult Result(JiraSyncStatus status, int projects = 2, int issues = 340, int comments = 1203,
        int removed = 0, TimeSpan? duration = null, string? error = null, params string[] warnings) =>
        new(status, projects, issues, comments, 1, 4, removed, duration ?? TimeSpan.FromSeconds(72), error, warnings);

    [Fact]
    public void Fetch_needs_a_connection()
    {
        var vm = TestViewModels.Fetch(TestSession.Create());

        Assert.False(vm.FetchCommand.CanExecute(null));
    }

    [Fact]
    public void Shows_what_is_stored_locally()
    {
        var sync = new FakeSyncService
        {
            Summary = new JiraDataSummary(5, 340, 1203, 12, 2, 8, new DateTimeOffset(2026, 10, 31, 5, 12, 0, TimeSpan.Zero), "https://acme.atlassian.net"),
        };

        var vm = TestViewModels.Fetch(Connected(), sync);

        Assert.StartsWith("5 projects · 340 issues · 1,203 comments · 8 sprints · last fetched 31 Oct 2026", vm.LocalDataText);
    }

    [Fact]
    public void Shows_nothing_fetched_yet_for_an_empty_database()
    {
        Assert.Equal("Nothing fetched yet", TestViewModels.Fetch(Connected()).LocalDataText);
    }

    [Fact]
    public async Task Fetch_passes_options_and_shows_a_summary()
    {
        var sync = new FakeSyncService { OnSync = (_, _, _) => Task.FromResult(Result(JiraSyncStatus.Completed)) };
        var vm = TestViewModels.Fetch(Connected(), sync);
        vm.JqlFilter = "updated >= -30d";
        vm.FullRefresh = true;

        await vm.FetchCommand.ExecuteAsync(null);

        var options = Assert.Single(sync.Runs);
        Assert.Null(options.ProjectKeys);
        Assert.Equal("updated >= -30d", options.JqlFilter);
        Assert.True(options.FullRefresh);
        Assert.Equal(FetchResultKind.Success, vm.ResultKind);
        Assert.Equal("Fetched 2 projects, 340 issues and 1,203 comments in 1 min 12 s.", vm.ResultMessage);
        Assert.False(vm.IsFetching);
    }

    [Fact]
    public async Task Selected_projects_are_loaded_and_only_checked_ones_are_fetched()
    {
        var sync = new FakeSyncService();
        sync.Projects.AddRange([TestData.Project("XYZ"), TestData.Project("ABC")]);
        var vm = TestViewModels.Fetch(Connected(), sync);

        vm.IsSelectedProjects = true;
        await Task.Yield();
        Assert.Equal(["ABC", "XYZ"], vm.Projects.Select(p => p.Key)); // sorted
        Assert.False(vm.FetchCommand.CanExecute(null)); // nothing ticked yet

        vm.Projects[1].IsSelected = true;
        Assert.Equal("1 of 2 selected", vm.SelectedCountText);
        Assert.True(vm.FetchCommand.CanExecute(null));

        await vm.FetchCommand.ExecuteAsync(null);
        Assert.Equal(["XYZ"], sync.Runs.Single().ProjectKeys);
    }

    [Fact]
    public async Task Select_all_and_none()
    {
        var sync = new FakeSyncService();
        sync.Projects.AddRange([TestData.Project("ABC"), TestData.Project("XYZ")]);
        var vm = TestViewModels.Fetch(Connected(), sync);
        await vm.LoadProjectsCommand.ExecuteAsync(null);

        vm.SelectAllProjectsCommand.Execute(null);
        Assert.All(vm.Projects, p => Assert.True(p.IsSelected));

        vm.SelectNoProjectsCommand.Execute(null);
        Assert.All(vm.Projects, p => Assert.False(p.IsSelected));
    }

    [Fact]
    public async Task Cancel_stops_the_running_fetch()
    {
        var started = new TaskCompletionSource();
        var sync = new FakeSyncService
        {
            OnSync = async (_, _, ct) =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
                return Result(JiraSyncStatus.Cancelled, projects: 1, issues: 120);
            },
        };
        var vm = TestViewModels.Fetch(Connected(), sync);

        var fetching = vm.FetchCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.IsFetching);
        Assert.True(vm.CancelCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);
        await fetching;

        Assert.Equal(FetchResultKind.Warning, vm.ResultKind);
        Assert.StartsWith("Fetch cancelled after 120 issues.", vm.ResultMessage);
    }

    [Fact]
    public async Task Disconnecting_cancels_a_running_fetch()
    {
        var cancelled = false;
        var started = new TaskCompletionSource();
        var session = Connected();
        var sync = new FakeSyncService
        {
            OnSync = async (_, _, ct) =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled = true; }
                return Result(JiraSyncStatus.Cancelled);
            },
        };
        var vm = TestViewModels.Fetch(session, sync);

        var fetching = vm.FetchCommand.ExecuteAsync(null);
        await started.Task;
        session.Disconnect();
        await fetching;

        Assert.True(cancelled);
    }

    [Theory]
    [InlineData(JiraSyncStatus.Completed, 0, 0, FetchResultKind.Success, "Everything is up to date")]
    [InlineData(JiraSyncStatus.Completed, 340, 3, FetchResultKind.Success, "Removed 3 issues deleted in Jira.")]
    [InlineData(JiraSyncStatus.Failed, 10, 0, FetchResultKind.Error, "Fetch stopped: Bad JQL. Data fetched before the error is kept.")]
    public async Task Result_messages(JiraSyncStatus status, int issues, int removed, FetchResultKind kind, string expected)
    {
        var sync = new FakeSyncService
        {
            OnSync = (_, _, _) => Task.FromResult(Result(status, issues: issues, removed: removed, error: status == JiraSyncStatus.Failed ? "Bad JQL." : null)),
        };
        var vm = TestViewModels.Fetch(Connected(), sync);

        await vm.FetchCommand.ExecuteAsync(null);

        Assert.Equal(kind, vm.ResultKind);
        Assert.Contains(expected, vm.ResultMessage);
    }

    [Fact]
    public async Task Warnings_are_listed_and_turn_the_result_amber()
    {
        var sync = new FakeSyncService
        {
            OnSync = (_, _, _) => Task.FromResult(Result(JiraSyncStatus.Completed, warnings: "Couldn't read the full user list")),
        };
        var vm = TestViewModels.Fetch(Connected(), sync);

        await vm.FetchCommand.ExecuteAsync(null);

        Assert.Equal(FetchResultKind.Warning, vm.ResultKind);
        Assert.Equal(["Couldn't read the full user list"], vm.ResultWarnings);
    }

    [Fact]
    public async Task Progress_text_and_bar_follow_the_fetch()
    {
        JiraFetchViewModel? vm = null;
        var snapshots = new List<(string Text, double Value, bool Indeterminate)>();
        var sync = new FakeSyncService
        {
            OnSync = async (_, progress, _) =>
            {
                foreach (var p in new[]
                {
                    new JiraSyncProgress(JiraSyncPhase.Projects, 0, 0, null, 0, null),
                    new JiraSyncProgress(JiraSyncPhase.Issues, 2, 4, "ABC", 50, 200),
                    new JiraSyncProgress(JiraSyncPhase.Issues, 3, 4, "XYZ", 7, null),
                    new JiraSyncProgress(JiraSyncPhase.Boards, 4, 4, "QA", 10, 10),
                })
                {
                    progress!.Report(p);
                    await Task.Delay(20); // Progress<T> posts to the captured context
                    snapshots.Add((vm!.ProgressText, vm.ProgressValue, vm.IsProgressIndeterminate));
                }

                return Result(JiraSyncStatus.Completed);
            },
        };
        vm = TestViewModels.Fetch(Connected(), sync);

        await vm.FetchCommand.ExecuteAsync(null);

        Assert.Equal(("Reading projects…", 0, true), snapshots[0]);
        Assert.Equal("Project 2 of 4 · ABC — Issues 50 of 200", snapshots[1].Text);
        Assert.Equal(31.25, snapshots[1].Value, 2); // (1 + 50/200) / 4
        Assert.Equal("Project 3 of 4 · XYZ — Issues 7", snapshots[2].Text);
        Assert.Equal(100, snapshots[3].Value);
    }

    [Theory]
    [InlineData(0.4, "less than a second")]
    [InlineData(8, "8 s")]
    [InlineData(72, "1 min 12 s")]
    [InlineData(3723, "1 h 2 min")]
    public void Durations_read_naturally(double seconds, string expected)
    {
        Assert.Equal(expected, JiraFetchViewModel.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }
}

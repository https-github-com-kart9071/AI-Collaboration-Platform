using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICollaborationPlatform.Tests;

public sealed class JiraSyncServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly JiraConnectionSettings Acme =
        new(new Uri("https://acme.atlassian.net"), JiraAuthType.Cloud, "alex@acme.com", "t");

    private readonly JiraDatabaseFactory _db = TestData.TempDatabase();
    private readonly FakeJiraClient _jira = new();
    private readonly ManualTimeProvider _time = new(Start);
    private readonly JiraSession _session = TestSession.Create();

    public JiraSyncServiceTests()
    {
        _session.Connect(Acme, new JiraUser("Alex Kim", null));
        _jira.Projects.AddRange([TestData.Project("ABC"), TestData.Project("XYZ")]);
        _jira.IssuesByProject["ABC"] = [TestData.Issue("1", "ABC-1", "ABC", [TestData.Comment("c1")]), TestData.Issue("2", "ABC-2", "ABC")];
        _jira.IssuesByProject["XYZ"] = [TestData.Issue("3", "XYZ-1", "XYZ")];
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(_db.Path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private JiraSyncService CreateService() =>
        new(_session, new FakeJiraClientFactory(_jira), _db, _time, NullLogger<JiraSyncService>.Instance);

    private long Count(string table)
    {
        using var db = _db.Open();
        return db.Count(table);
    }

    [Fact]
    public async Task First_fetch_stores_everything_and_reports_a_summary()
    {
        _jira.Users.Add(new JiraPerson("acc-sam", "Sam", null, true));
        _jira.BoardsByProject["ABC"] = [new JiraBoard(10, "ABC board", "scrum", "ABC")];
        _jira.SprintsByBoard[10] = [new JiraSprint(100, "Sprint 1", "active", Start, null, null, null, 10)];

        var result = await CreateService().SyncAsync(new JiraSyncOptions());

        Assert.Equal(JiraSyncStatus.Completed, result.Status);
        Assert.Equal((2, 3, 1, 1, 1), (result.Projects, result.Issues, result.Comments, result.Boards, result.Sprints));
        Assert.Empty(result.Warnings);
        Assert.Equal(3, Count("issues"));
        Assert.Equal(1, Count("comments"));
        Assert.Equal(1, Count("sprints"));
        Assert.Equal(3, Count("users")); // Sam from the user list + Alex and Riya from issues
        Assert.All(_jira.SearchedWithChangelog, Assert.True);
        Assert.Equal("project = \"ABC\" ORDER BY updated ASC", _jira.SearchedJql[0]);

        var summary = CreateService().GetSummary();
        Assert.Equal(2, summary.Projects);
        Assert.Equal("https://acme.atlassian.net", summary.Site);
        Assert.Equal(Start, summary.LastFetchedAt);
    }

    [Fact]
    public async Task Only_one_fetch_runs_at_a_time()
    {
        var hold = new TaskCompletionSource();
        _jira.HoldFields = hold;
        var service = CreateService();

        var first = service.SyncAsync(new JiraSyncOptions());
        var second = await service.SyncAsync(new JiraSyncOptions());

        Assert.Equal(JiraSyncStatus.Failed, second.Status);
        Assert.Contains("already running", second.Error);
        hold.SetResult();
        Assert.Equal(JiraSyncStatus.Completed, (await first).Status);
        Assert.Equal(JiraSyncStatus.Completed, (await service.SyncAsync(new JiraSyncOptions())).Status); // free again afterwards
    }

    [Fact]
    public async Task Fetches_only_selected_projects_and_warns_about_unknown_ones()
    {
        var result = await CreateService().SyncAsync(new JiraSyncOptions(["xyz", "NOPE"]));

        Assert.Equal(1, result.Projects);
        Assert.Single(_jira.SearchedJql);
        Assert.StartsWith("project = \"XYZ\"", _jira.SearchedJql[0]);
        Assert.Contains(result.Warnings, w => w.Contains("NOPE"));
    }

    [Fact]
    public async Task Second_fetch_asks_only_for_issues_updated_since_the_first()
    {
        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));
        _jira.SearchedJql.Clear();
        _time.Now = Start.AddMinutes(90);

        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));

        // 90 minutes since the last fetch + 5 minutes overlap.
        Assert.Equal("project = \"ABC\" AND updated >= -95m ORDER BY updated ASC", _jira.SearchedJql.Single());
    }

    [Fact]
    public async Task Full_refresh_ignores_the_last_fetch()
    {
        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));
        _jira.SearchedJql.Clear();

        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"], FullRefresh: true));

        Assert.DoesNotContain("updated >=", _jira.SearchedJql.Single());
    }

    [Fact]
    public async Task Filter_is_combined_with_the_project_and_a_changed_filter_fetches_fully()
    {
        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"], "status != Done"));
        Assert.Equal("project = \"ABC\" AND (status != Done) ORDER BY updated ASC", _jira.SearchedJql[0]);

        _time.Now = Start.AddMinutes(10);
        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"], "status != Done"));
        Assert.Contains("AND (status != Done) AND updated >= -15m", _jira.SearchedJql[1]);

        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"], "assignee = currentUser()"));
        Assert.DoesNotContain("updated >=", _jira.SearchedJql[2]);
    }

    [Fact]
    public async Task Remaining_comments_are_fetched_when_jira_inlines_only_some()
    {
        _jira.IssuesByProject["ABC"] = [TestData.Issue("1", "ABC-1", "ABC", [TestData.Comment("c1")], commentTotal: 3)];
        _jira.AllComments["ABC-1"] = [TestData.Comment("c1"), TestData.Comment("c2"), TestData.Comment("c3")];

        var result = await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));

        Assert.Equal(["ABC-1"], _jira.CommentRequests);
        Assert.Equal(3, result.Comments);
        Assert.Equal(3, Count("comments"));
    }

    [Fact]
    public async Task Full_unfiltered_fetch_removes_issues_deleted_in_jira()
    {
        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));
        _jira.IssuesByProject["ABC"].RemoveAt(1);

        var result = await CreateService().SyncAsync(new JiraSyncOptions(["ABC"], FullRefresh: true));

        Assert.Equal(1, result.IssuesRemoved);
        Assert.Equal(1, Count("issues"));
    }

    [Fact]
    public async Task Refresh_removes_issues_deleted_in_jira_even_though_they_never_show_as_updated()
    {
        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));
        _jira.IssuesByProject["ABC"].Clear();          // nothing was updated…
        _jira.ExistingIssueIds["ABC"] = ["1"];          // …but ABC-2 was deleted
        _time.Now = Start.AddMinutes(30);

        var result = await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));

        Assert.Equal(1, result.IssuesRemoved);
        Assert.Equal(1, Count("issues"));
        Assert.Equal("project = \"ABC\" ORDER BY updated ASC", _jira.IdQueries.Single()); // the whole project, IDs only
    }

    [Fact]
    public async Task Refresh_with_nothing_deleted_keeps_everything()
    {
        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));
        _jira.IssuesByProject["ABC"].Clear(); // nothing changed, nothing deleted
        _jira.ExistingIssueIds["ABC"] = ["1", "2"];

        var result = await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));

        Assert.Equal(0, result.IssuesRemoved);
        Assert.Equal(2, Count("issues"));
    }

    [Fact]
    public async Task Filtered_fetch_never_removes_issues()
    {
        // With a JQL filter the local copy is only part of the project, so missing issues may just not match it.
        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"], "status != Done"));
        _jira.ExistingIssueIds["ABC"] = [];

        var result = await CreateService().SyncAsync(new JiraSyncOptions(["ABC"], "status != Done"));

        Assert.Equal(0, result.IssuesRemoved);
        Assert.Equal(2, Count("issues"));
        Assert.Empty(_jira.IdQueries);
    }

    [Fact]
    public async Task If_jira_cant_list_the_issues_nothing_is_removed_and_it_says_so()
    {
        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));
        _jira.IdsError = TestData.Forbidden();

        var result = await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));

        Assert.Equal(JiraSyncStatus.Completed, result.Status);
        Assert.Equal(0, result.IssuesRemoved);
        Assert.Equal(2, Count("issues"));
        Assert.StartsWith("Couldn't check ABC for deleted issues", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task Cancelling_keeps_saved_data_and_refetches_the_unfinished_project_next_time()
    {
        using var cts = new CancellationTokenSource();
        _jira.BeforeIssue = issue => { if (issue.Key == "XYZ-1") cts.Cancel(); };

        var result = await CreateService().SyncAsync(new JiraSyncOptions(), cancellationToken: cts.Token);

        Assert.Equal(JiraSyncStatus.Cancelled, result.Status);
        Assert.Equal(1, result.Projects); // ABC finished, XYZ didn't
        Assert.Equal(2, Count("issues"));
        using (var db = _db.Open())
        {
            Assert.NotNull(db.GetProjectSyncState("ABC").SyncedAt);
            Assert.Null(db.GetProjectSyncState("XYZ").SyncedAt);
        }

        _jira.BeforeIssue = null;
        _jira.SearchedJql.Clear();
        await CreateService().SyncAsync(new JiraSyncOptions());
        Assert.Contains("updated >=", _jira.SearchedJql[0]); // ABC: incremental
        Assert.DoesNotContain("updated >=", _jira.SearchedJql[1]); // XYZ: full again
        Assert.Equal(3, Count("issues"));
    }

    [Fact]
    public async Task Jira_error_fails_the_fetch_with_its_message_but_keeps_finished_projects()
    {
        var search = 0;
        _jira.BeforeIssue = _ => { if (++search == 3) throw new JiraApiException(System.Net.HttpStatusCode.BadRequest, "Bad JQL.", []); };

        var result = await CreateService().SyncAsync(new JiraSyncOptions());

        Assert.Equal(JiraSyncStatus.Failed, result.Status);
        Assert.Equal("Bad JQL.", result.Error);
        Assert.Equal(2, Count("issues"));
    }

    [Fact]
    public async Task Missing_permission_for_users_or_boards_only_warns()
    {
        _jira.UsersError = TestData.Forbidden();
        _jira.BoardsError = TestData.Forbidden();

        var result = await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));

        Assert.Equal(JiraSyncStatus.Completed, result.Status);
        Assert.Contains(result.Warnings, w => w.Contains("user list"));
        Assert.Contains(result.Warnings, w => w.Contains("boards for ABC"));
        Assert.Equal(2, Count("users")); // still taken from issues
    }

    [Fact]
    public async Task Switching_jira_sites_clears_the_old_sites_data()
    {
        await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));
        _session.Connect(Acme with { BaseUrl = new Uri("https://other.atlassian.net") }, new JiraUser("Alex", null));
        _jira.IssuesByProject["ABC"] = [TestData.Issue("9", "ABC-9", "ABC")];

        var result = await CreateService().SyncAsync(new JiraSyncOptions(["ABC"]));

        Assert.Contains(result.Warnings, w => w.Contains("acme.atlassian.net was removed"));
        Assert.Equal(1, Count("issues"));
        Assert.DoesNotContain("updated >=", _jira.SearchedJql.Last());
    }

    [Fact]
    public async Task Progress_reports_each_phase_and_issue_counts()
    {
        var reports = new List<JiraSyncProgress>();
        var progress = new SynchronousProgress<JiraSyncProgress>(reports.Add);

        await CreateService().SyncAsync(new JiraSyncOptions(), progress);

        Assert.Equal(JiraSyncPhase.Fields, reports[0].Phase);
        Assert.Contains(reports, r => r is { Phase: JiraSyncPhase.Issues, ProjectIndex: 1, ProjectCount: 2, ProjectKey: "ABC", IssuesDone: 2, IssuesTotal: 2 });
        Assert.Contains(reports, r => r is { Phase: JiraSyncPhase.Issues, ProjectIndex: 2, ProjectKey: "XYZ", IssuesDone: 1 });
        Assert.Equal(JiraSyncPhase.Boards, reports[^1].Phase);
    }

    [Fact]
    public void Summary_without_a_database_file_is_empty_and_creates_nothing()
    {
        var summary = CreateService().GetSummary();

        Assert.Equal(0, summary.Issues);
        Assert.Null(summary.LastFetchedAt);
        Assert.False(File.Exists(_db.Path));
    }

    [Theory]
    [InlineData(0, "5m")]           // just fetched: only the 5-minute overlap
    [InlineData(30, "35m")]
    [InlineData(60 * 24 * 3, "4325m")]
    [InlineData(-10, "1m")]         // clock moved backwards: still at least a minute
    public void Incremental_jql_uses_relative_minutes(int minutesAgo, string expected)
    {
        var jql = JiraSyncService.BuildJql("ABC", null, Start.AddMinutes(-minutesAgo), Start);

        Assert.Equal($"project = \"ABC\" AND updated >= -{expected} ORDER BY updated ASC", jql);
    }

    [Fact]
    public void Project_keys_with_quotes_are_escaped()
    {
        Assert.StartsWith("project = \"A\\\"B\"", JiraSyncService.BuildJql("A\"B", null, null, Start));
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

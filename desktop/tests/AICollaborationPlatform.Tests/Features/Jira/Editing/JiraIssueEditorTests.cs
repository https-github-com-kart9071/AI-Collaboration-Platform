using System.Net;
using System.Net.Http;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICollaborationPlatform.Tests;

public sealed class JiraIssueEditorTests : IDisposable
{
    private const string Site = "https://acme.atlassian.net";
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly JiraConnectionSettings Acme = new(new Uri(Site), JiraAuthType.Cloud, "alex@acme.com", "t");
    private static readonly JiraTransition StartProgress = new("21", "Start progress", "In Progress", "indeterminate");

    private readonly JiraDatabaseFactory _db = TestData.TempDatabase();
    private readonly FakeJiraClient _jira = new();
    private readonly JiraSession _session = TestSession.Create();

    public JiraIssueEditorTests()
    {
        _session.Connect(Acme, new JiraUser("Alex Kim", null));
        _jira.Users.AddRange([TestData.Alex, TestData.Riya]);
        _jira.Transitions.Add(StartProgress);

        // The same issue in Jira and in the local copy.
        var issue = TestData.Issue("1", "ABC-1", "ABC", summary: "Login fails");
        _jira.IssuesByProject["ABC"] = [issue];
        using var db = _db.Open();
        db.SetSite(Site);
        db.SaveIssue(issue, [], Now);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(_db.Path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private JiraIssueEditor CreateEditor(IJiraSession? session = null) =>
        new(session ?? _session, new FakeJiraClientFactory(_jira), _db, TimeProvider.System, NullLogger<JiraIssueEditor>.Instance);

    private IssueDetail LocalCopy()
    {
        using var db = _db.Open();
        return db.GetIssueDetail("1")!;
    }

    private static DateTime? KnownUpdated(IssueDetail detail) => detail.Updated;

    [Fact]
    public async Task Adding_a_comment_sends_it_then_saves_the_issue_locally()
    {
        var result = await CreateEditor().AddCommentAsync("ABC-1", "  Looks good to me  ");

        Assert.Equal(new IssueEditResult(IssueEditStatus.Saved), result);
        Assert.Equal(["comment ABC-1 Looks good to me"], _jira.Changes);
        Assert.Equal("Looks good to me", Assert.Single(LocalCopy().Comments).Body);
    }

    [Fact]
    public async Task Nothing_is_sent_when_not_connected()
    {
        var result = await CreateEditor(TestSession.Create()).AddCommentAsync("ABC-1", "Hello");

        Assert.Equal(IssueEditStatus.Failed, result.Status);
        Assert.Equal("Connect to Jira on the Jira page to make changes.", result.Message);
        Assert.Empty(_jira.Changes);
    }

    [Fact]
    public async Task Data_from_another_jira_site_is_never_changed()
    {
        using (var db = _db.Open()) db.SetSite("https://old.atlassian.net");

        var result = await CreateEditor().UpdateDetailsAsync("ABC-1", new IssueDetailsChange(Priority: "High"));

        Assert.Equal(IssueEditStatus.Failed, result.Status);
        Assert.Contains("https://old.atlassian.net", result.Message);
        Assert.Empty(_jira.Changes);
    }

    [Theory]
    [InlineData(Site, null)]
    [InlineData(Site + "/", null)]
    [InlineData("HTTPS://ACME.atlassian.net", null)]
    [InlineData(null, null)]
    [InlineData("https://other.atlassian.net", "This data is from https://other.atlassian.net, but you're connected to https://acme.atlassian.net. Fetch data again to make changes.")]
    public void Editing_is_allowed_only_for_data_from_the_connected_site(string? dataSite, string? expected) =>
        Assert.Equal(expected, CreateEditor().CannotEditReason(dataSite));

    [Fact]
    public async Task Summary_is_saved_when_jira_has_no_newer_version()
    {
        var result = await CreateEditor().UpdateTextAsync("ABC-1", new JiraIssueUpdate(Summary: " Login fails on Safari "), KnownUpdated(LocalCopy()));

        Assert.True(result.Succeeded);
        Assert.Equal("Login fails on Safari", LocalCopy().Summary);
        Assert.Single(_jira.Changes);
    }

    [Fact]
    public async Task Text_isnt_saved_over_a_newer_version_in_jira()
    {
        var known = KnownUpdated(LocalCopy());
        var list = _jira.IssuesByProject["ABC"];
        list[0] = list[0] with { Summary = "Renamed by Riya", Updated = list[0].Updated!.Value.AddMinutes(10) };

        var result = await CreateEditor().UpdateTextAsync("ABC-1", new JiraIssueUpdate(Summary: "My new title"), known);

        Assert.Equal(IssueEditStatus.Conflict, result.Status);
        Assert.StartsWith("ABC-1 was changed in Jira since this copy was fetched", result.Message);
        Assert.Empty(_jira.Changes); // nothing overwritten
        Assert.Equal("Renamed by Riya", LocalCopy().Summary); // and the app now shows the newer version
    }

    [Fact]
    public async Task Empty_summary_is_refused()
    {
        var result = await CreateEditor().UpdateTextAsync("ABC-1", new JiraIssueUpdate(Summary: "   "), null);

        Assert.Equal(new IssueEditResult(IssueEditStatus.Failed, "The summary can't be empty."), result);
        Assert.Empty(_jira.Changes);
    }

    [Fact]
    public async Task Details_are_applied_priority_then_assignee_then_status_and_saved_locally()
    {
        var change = new IssueDetailsChange(Priority: "High", Assign: true, Assignee: TestData.Riya, Transition: StartProgress);

        var result = await CreateEditor().UpdateDetailsAsync("ABC-1", change);

        Assert.True(result.Succeeded);
        Assert.Equal(3, _jira.Changes.Count);
        Assert.StartsWith("update ABC-1", _jira.Changes[0]);
        Assert.Equal(["assign ABC-1 acc-riya", "transition ABC-1 21"], _jira.Changes[1..]);
        var local = LocalCopy();
        Assert.Equal(("High", "Riya Shah", "In Progress", "indeterminate"), (local.Priority, local.Assignee, local.Status, local.StatusCategory));
    }

    [Fact]
    public async Task Unassigning_sends_no_one()
    {
        var result = await CreateEditor().UpdateDetailsAsync("ABC-1", new IssueDetailsChange(Assign: true, Assignee: null));

        Assert.True(result.Succeeded);
        Assert.Equal(["assign ABC-1 nobody"], _jira.Changes);
        Assert.Null(LocalCopy().Assignee);
    }

    [Fact]
    public async Task A_failed_step_says_what_was_saved_before_it()
    {
        _jira.FailOn["transition"] = new JiraApiException(HttpStatusCode.BadRequest, "Jira returned 400 Bad Request: ...",
            ["Field 'resolution' is required."]);

        var result = await CreateEditor().UpdateDetailsAsync("ABC-1", new IssueDetailsChange(Priority: "High", Transition: StartProgress));

        Assert.Equal(IssueEditStatus.Failed, result.Status);
        Assert.StartsWith("Saved the priority, but couldn't change the status: Jira didn't accept it: Field 'resolution' is required.", result.Message);
        Assert.Contains("open the issue in Jira", result.Message);
        Assert.Equal("High", LocalCopy().Priority); // the saved part shows up straight away
    }

    public static TheoryData<Exception, string> Errors => new()
    {
        { new JiraApiException(HttpStatusCode.Forbidden, "x", []), "Couldn't add the comment: Your Jira account doesn't have permission to do this." },
        { new JiraApiException(HttpStatusCode.Unauthorized, "x", []), "Couldn't add the comment: Jira rejected the saved credentials. Reconnect on the Jira page, then try again." },
        { new JiraApiException(HttpStatusCode.NotFound, "x", []), "Couldn't add the comment: Jira couldn't find the issue. It may have been deleted or moved, or you can no longer see it." },
        { new HttpRequestException("No such host"), "Couldn't add the comment: Couldn't reach Jira. Check your internet connection, then try again." },
        { new TaskCanceledException("Timed out"), "Couldn't add the comment: Couldn't reach Jira. Check your internet connection, then try again." },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task Failures_are_explained_in_plain_words(Exception error, string expected)
    {
        _jira.FailOn["comment"] = error;

        var result = await CreateEditor().AddCommentAsync("ABC-1", "Hello");

        Assert.Equal(new IssueEditResult(IssueEditStatus.Failed, expected), result);
        Assert.Empty(LocalCopy().Comments);
    }

    [Fact]
    public async Task A_change_that_worked_but_couldnt_be_reread_is_saved_with_a_warning()
    {
        _jira.FailOn["get"] = new HttpRequestException("Connection reset");

        var result = await CreateEditor().AddCommentAsync("ABC-1", "Hello");

        Assert.Equal(IssueEditStatus.Saved, result.Status);
        Assert.Equal("Saved in Jira, but the app's copy couldn't be updated. Use Refresh to see the change.", result.Message);
    }

    [Fact]
    public async Task Choices_come_from_jira_and_a_failing_list_doesnt_hide_the_others()
    {
        _jira.FailOn["assignable"] = TestData.Forbidden();

        var choices = await CreateEditor().GetChoicesAsync("ABC-1");

        Assert.Equal([StartProgress], choices.Transitions);
        Assert.Equal(5, choices.Priorities.Count);
        Assert.Empty(choices.Assignees);
        Assert.Equal("Couldn't load people: Your Jira account doesn't have permission to do this.", choices.Error);
    }

    [Fact]
    public async Task Searching_people_asks_jira_with_the_text()
    {
        var found = await CreateEditor().SearchAssigneesAsync("ABC-1", "riya");

        Assert.Equal([TestData.Riya], found);
        Assert.Equal(["riya"], _jira.AssignableQueries);
    }
}

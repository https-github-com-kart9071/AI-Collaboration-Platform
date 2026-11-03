using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Storage;
using Microsoft.Data.Sqlite;

namespace AICollaborationPlatform.Tests;

public sealed class JiraDatabaseTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
    private readonly JiraDatabaseFactory _factory = TestData.TempDatabase();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(_factory.Path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static List<string> Column(JiraDatabase db, string sql)
    {
        // Small helper for asserting on raw table contents.
        var connection = typeof(JiraDatabase).GetField("_connection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(db) as SqliteConnection;
        using var command = connection!.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.IsDBNull(0) ? "<null>" : reader.GetValue(0).ToString()!);
        return values;
    }

    [Fact]
    public void Creates_schema_in_a_new_file_and_reopens_it()
    {
        using (var db = _factory.Open())
        {
            Assert.Equal(0, db.Count("issues"));
        }

        using var again = _factory.Open();
        Assert.Equal(0, again.Count("projects"));
        Assert.True(File.Exists(_factory.Path));
    }

    [Fact]
    public void Saves_issue_with_comments_attachments_history_and_people()
    {
        using var db = _factory.Open();
        var issue = TestData.Issue("1", "ABC-1", "ABC",
            attachments: [new JiraAttachment("a1", "log.txt", 120, "text/plain", Now, TestData.Alex, "https://x/a1")],
            changes: [new JiraChange("h1", TestData.Riya, Now, "status", "To Do", "Done")]);

        db.SaveIssue(issue, [TestData.Comment("c1"), TestData.Comment("c2")], Now);

        Assert.Equal(1, db.Count("issues"));
        Assert.Equal(2, db.Count("comments"));
        Assert.Equal(["log.txt"], Column(db, "SELECT filename FROM attachments"));
        Assert.Equal(["Done"], Column(db, "SELECT to_value FROM changelog"));
        Assert.Equal(["acc-alex", "acc-riya"], Column(db, "SELECT id FROM users ORDER BY id"));
        Assert.Equal(["[\"label1\"]"], Column(db, "SELECT labels FROM issues"));
        Assert.Contains("customfield_1", Column(db, "SELECT fields_json FROM issues").Single());
        Assert.Equal(["2024-02-01T10:00:00.0000000+00:00"], Column(db, "SELECT updated FROM issues"));
    }

    [Fact]
    public void Saving_again_updates_and_replaces_children()
    {
        using var db = _factory.Open();
        db.SaveIssue(TestData.Issue("1", "ABC-1", "ABC", summary: "Old"), [TestData.Comment("c1"), TestData.Comment("c2")], Now);

        db.SaveIssue(TestData.Issue("1", "ABC-1", "ABC", summary: "New"), [TestData.Comment("c3")], Now);

        Assert.Equal(["New"], Column(db, "SELECT summary FROM issues"));
        Assert.Equal(["c3"], Column(db, "SELECT id FROM comments"));
    }

    [Fact]
    public void Upserting_a_user_keeps_a_known_email_when_the_new_one_is_missing()
    {
        using var db = _factory.Open();
        db.UpsertUser(new JiraPerson("u1", "Alex", "alex@acme.com", true));

        db.UpsertUser(new JiraPerson("u1", "Alex K", null, false));

        Assert.Equal(["alex@acme.com"], Column(db, "SELECT email FROM users"));
        Assert.Equal(["Alex K"], Column(db, "SELECT display_name FROM users"));
    }

    [Fact]
    public void Failed_transaction_saves_nothing()
    {
        using var db = _factory.Open();

        Assert.Throws<InvalidOperationException>(() => db.InTransaction(() =>
        {
            db.SaveIssue(TestData.Issue("1", "ABC-1", "ABC"), [], Now);
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal(0, db.Count("issues"));
    }

    [Fact]
    public void Deleting_missing_issues_also_removes_their_children()
    {
        using var db = _factory.Open();
        db.SaveIssue(TestData.Issue("1", "ABC-1", "ABC"), [TestData.Comment("c1")], Now);
        db.SaveIssue(TestData.Issue("2", "ABC-2", "ABC"), [TestData.Comment("c2")], Now);
        db.SaveIssue(TestData.Issue("3", "XYZ-1", "XYZ"), [], Now);

        var removed = db.DeleteIssuesNotIn("ABC", ["1"]);

        Assert.Equal(1, removed);
        Assert.Equal(["ABC-1", "XYZ-1"], Column(db, "SELECT key FROM issues ORDER BY key"));
        Assert.Equal(["c1"], Column(db, "SELECT id FROM comments"));
    }

    [Fact]
    public void Project_sync_state_round_trips()
    {
        using var db = _factory.Open();
        db.UpsertProject(TestData.Project("ABC"), Now);
        Assert.Equal((null, null), db.GetProjectSyncState("ABC"));

        db.MarkProjectSynced("ABC", Now, "status != Done");

        Assert.Equal((Now, "status != Done"), db.GetProjectSyncState("ABC"));
        Assert.Equal((null, null), db.GetProjectSyncState("NOPE"));
    }

    [Fact]
    public void Reused_project_key_replaces_the_old_project()
    {
        using var db = _factory.Open();
        db.UpsertProject(TestData.Project("ABC", id: "old"), Now);

        db.UpsertProject(TestData.Project("ABC", id: "new"), Now);

        Assert.Equal(["new"], Column(db, "SELECT id FROM projects"));
    }

    [Fact]
    public void Summary_reports_counts_site_and_last_completed_fetch()
    {
        using var db = _factory.Open();
        db.SetSite("https://acme.atlassian.net");
        db.UpsertProject(TestData.Project("ABC"), Now);
        db.MarkProjectSynced("ABC", Now, null);
        db.SaveIssue(TestData.Issue("1", "ABC-1", "ABC"), [TestData.Comment("c1")], Now);
        db.UpsertBoard(new JiraBoard(1, "Board", "scrum", "ABC"));
        db.UpsertSprint(new JiraSprint(5, "Sprint 1", "active", Now, null, null, null, 1));
        var completed = db.StartSyncRun(Now, null);
        db.FinishSyncRun(completed, "completed", Now.AddMinutes(2), 1, 1, 1, null);
        var cancelled = db.StartSyncRun(Now.AddHours(1), null);
        db.FinishSyncRun(cancelled, "cancelled", Now.AddHours(2), 0, 0, 0, null);

        var summary = db.GetSummary();

        Assert.Equal(new JiraDataSummary(1, 1, 1, 2, 1, 1, Now.AddMinutes(2), "https://acme.atlassian.net"), summary);
    }

    [Fact]
    public void Browse_lists_fetched_projects_with_issue_counts()
    {
        using var db = _factory.Open();
        db.UpsertProject(TestData.Project("ABC"), Now);
        db.UpsertProject(TestData.Project("XYZ"), Now);
        db.UpsertProject(TestData.Project("NEVER"), Now); // listed in Jira but never fetched
        db.MarkProjectSynced("ABC", Now, null);
        db.SaveIssue(TestData.Issue("1", "ABC-1", "ABC"), [], Now);
        db.SaveIssue(TestData.Issue("2", "ABC-2", "ABC"), [], Now);
        db.SaveIssue(TestData.Issue("3", "XYZ-1", "XYZ"), [], Now); // from a cancelled fetch: still shown

        var projects = db.GetProjects();

        Assert.Equal([new ProjectRow("ABC", "ABC project", 2), new ProjectRow("XYZ", "XYZ project", 1)], projects);
    }

    [Fact]
    public void Browse_issue_rows_carry_table_columns_in_local_time()
    {
        using var db = _factory.Open();
        db.SaveIssue(TestData.Issue("1", "ABC-12", "ABC", summary: "Login fails", assignee: TestData.Riya), [], Now);

        var row = Assert.Single(db.GetIssueRows());

        Assert.Equal(("ABC-12", "ABC", "Login fails", "Task", "To Do", "new", "Riya Shah", "Medium"),
            (row.Key, row.ProjectKey, row.Summary, row.Type, row.Status, row.StatusCategory, row.Assignee, row.Priority));
        Assert.Equal(new DateTimeOffset(2024, 2, 1, 10, 0, 0, TimeSpan.Zero).LocalDateTime, row.Updated);
        Assert.Equal(12, row.KeyNumber);
    }

    [Fact]
    public void Browse_issue_detail_includes_comments_history_and_attachments()
    {
        using var db = _factory.Open();
        var issue = TestData.Issue("1", "ABC-1", "ABC",
            attachments: [new JiraAttachment("a1", "log.txt", 2048, "text/plain", Now, TestData.Alex, "https://x/a1")],
            changes:
            [
                new JiraChange("h1", TestData.Riya, Now.AddDays(-2), "status", "To Do", "In Progress"),
                new JiraChange("h2", TestData.Alex, Now.AddDays(-1), "status", "In Progress", "Done"),
            ]);
        db.SaveIssue(issue, [TestData.Comment("c1", "First"), TestData.Comment("c2", "Second")], Now);

        var detail = db.GetIssueDetail("1")!;

        Assert.Equal(("ABC-1", "Description text", "Riya Shah", "label1"), (detail.Key, detail.Description, detail.Reporter, detail.LabelsText));
        Assert.Equal(["Second", "First"], detail.Comments.Select(c => c.Body)); // newest first, under the new-comment box
        Assert.Equal(["Done", "In Progress"], detail.Changes.Select(c => c.To)); // newest first
        Assert.Equal("2 KB", Assert.Single(detail.Attachments).SizeText);
        Assert.Null(db.GetIssueDetail("nope"));
    }

    [Fact]
    public void Loading_10000_issue_rows_is_quick()
    {
        using var db = _factory.Open();
        db.InTransaction(() =>
        {
            for (var i = 1; i <= 10_000; i++)
            {
                db.SaveIssue(TestData.Issue($"{i}", $"BIG-{i}", "BIG"), [], Now);
            }
        });

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var rows = db.GetIssueRows();
        timer.Stop();

        Assert.Equal(10_000, rows.Count);
        Assert.True(timer.ElapsedMilliseconds < 2000, $"Took {timer.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void Attachment_sizes_read_naturally(long size, string expected)
    {
        Assert.Equal(expected, new AttachmentRow("f", size, null, null, null).SizeText);
    }

    [Fact]
    public void Clearing_removes_all_jira_data_but_keeps_run_history()
    {
        using var db = _factory.Open();
        db.UpsertProject(TestData.Project("ABC"), Now);
        db.SaveIssue(TestData.Issue("1", "ABC-1", "ABC"), [TestData.Comment("c1")], Now);
        db.UpsertField(new JiraField("summary", "Summary", false, "string"));
        db.StartSyncRun(Now, null);

        db.ClearAllData();

        foreach (var table in new[] { "projects", "issues", "comments", "users", "fields" })
        {
            Assert.Equal(0, db.Count(table));
        }

        Assert.Equal(1, db.Count("sync_runs"));
    }
}

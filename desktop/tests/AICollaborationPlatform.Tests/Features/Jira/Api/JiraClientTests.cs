using System.Net;
using System.Net.Http;
using System.Text;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICollaborationPlatform.Tests;

public class JiraClientTests
{
    private static readonly JiraConnectionSettings Cloud =
        new(new Uri("https://acme.atlassian.net"), JiraAuthType.Cloud, "alex@acme.com", "cloud-token");

    private static readonly JiraConnectionSettings Server =
        new(new Uri("https://acme.com/jira"), JiraAuthType.Server, null, "server-pat");

    private static JiraClient CreateClient(JiraApiStub stub, JiraConnectionSettings settings, int pageSize = 2) =>
        new(new HttpClient(stub), settings, NullLogger<JiraClient>.Instance, pageSize);

    // ---- Auth ----

    [Fact]
    public async Task Cloud_requests_use_basic_auth_and_server_requests_use_bearer()
    {
        var cloudStub = new JiraApiStub().On("/rest/api/3/field", "[]");
        var serverStub = new JiraApiStub().On("/jira/rest/api/2/field", "[]");

        await CreateClient(cloudStub, Cloud).GetFieldsAsync();
        await CreateClient(serverStub, Server).GetFieldsAsync();

        var basic = cloudStub.Requests.Single().Headers.Authorization!;
        Assert.Equal("Basic", basic.Scheme);
        Assert.Equal("alex@acme.com:cloud-token", Encoding.UTF8.GetString(Convert.FromBase64String(basic.Parameter!)));
        Assert.Equal("Bearer server-pat", serverStub.Requests.Single().Headers.Authorization!.ToString());
        Assert.Equal("application/json", cloudStub.Requests.Single().Headers.Accept.Single().MediaType);
    }

    // ---- Projects ----

    [Fact]
    public async Task Cloud_projects_page_through_project_search_until_isLast()
    {
        var stub = new JiraApiStub()
            .On("/rest/api/3/project/search", """
                {"startAt":0,"maxResults":2,"total":3,"isLast":false,"values":[
                  {"id":"10000","key":"ABC","name":"Alpha","projectTypeKey":"software","lead":{"accountId":"a1","displayName":"Alex"}},
                  {"id":"10001","key":"DEF","name":"Delta"}]}
                """, queryContains: "startAt=0")
            .On("/rest/api/3/project/search", """
                {"startAt":2,"maxResults":2,"total":3,"isLast":true,"values":[{"id":"10002","key":"GHI","name":"Gamma"}]}
                """, queryContains: "startAt=2");

        var projects = await CreateClient(stub, Cloud).GetProjectsAsync().CollectAsync();

        Assert.Equal(["ABC", "DEF", "GHI"], projects.Select(p => p.Key));
        Assert.Equal(new JiraPerson("a1", "Alex", null, true), projects[0].Lead);
        Assert.Equal("software", projects[0].ProjectTypeKey);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Contains("maxResults=2", stub.RequestedPathsAndQueries.First());
    }

    [Fact]
    public async Task Server_projects_come_from_one_array_under_the_context_path()
    {
        var stub = new JiraApiStub().On("/jira/rest/api/2/project", """
            [{"id":"1","key":"ABC","name":"Alpha","lead":{"key":"JIRAUSER1","name":"alex","displayName":"Alex"}}]
            """);

        var projects = await CreateClient(stub, Server).GetProjectsAsync().CollectAsync();

        var project = Assert.Single(projects);
        Assert.Equal("JIRAUSER1", project.Lead!.Id);
        Assert.Equal("/jira/rest/api/2/project", stub.Requests.Single().RequestUri!.AbsolutePath);
    }

    // ---- Issue search ----

    private const string CloudIssue = """
        {"id":"10100","key":"ABC-1","fields":{
          "summary":"Login fails","project":{"key":"ABC"},"issuetype":{"name":"Bug"},
          "status":{"name":"In Progress","statusCategory":{"key":"indeterminate"}},"priority":{"name":"High"},
          "assignee":{"accountId":"a1","displayName":"Alex Kim","emailAddress":"alex@acme.com","active":true},
          "reporter":{"accountId":"r1","displayName":"Riya"},
          "created":"2024-01-15T10:30:00.000+0530","updated":"2024-02-01T08:00:00.000+0000","resolutiondate":null,
          "labels":["auth","p1"],"parent":{"key":"ABC-0"},"customfield_10016":5,
          "description":{"type":"doc","version":1,"content":[
            {"type":"paragraph","content":[{"type":"text","text":"Steps:"},{"type":"hardBreak"},{"type":"text","text":"1. Open app"}]},
            {"type":"paragraph","content":[{"type":"mention","attrs":{"text":"@Alex"}},{"type":"text","text":" please check"}]}]}}}
        """;

    [Fact]
    public async Task Cloud_search_uses_search_jql_with_next_page_token()
    {
        var stub = new JiraApiStub()
            .On("/rest/api/3/search/jql", $$"""{"issues":[{{CloudIssue}}],"nextPageToken":"tok-2","isLast":false}""")
            .On("/rest/api/3/search/jql", """{"issues":[{"id":"10101","key":"ABC-2","fields":{"summary":"Second"}}],"isLast":true}""",
                queryContains: "nextPageToken=tok-2");

        var issues = await CreateClient(stub, Cloud).SearchIssuesAsync("project = ABC ORDER BY updated").CollectAsync();

        Assert.Equal(["ABC-1", "ABC-2"], issues.Select(i => i.Key));
        var first = stub.RequestedPathsAndQueries.First();
        Assert.Contains("jql=project = ABC ORDER BY updated", first);
        Assert.Contains("fields=*all", first);
        Assert.DoesNotContain("nextPageToken", first);
    }

    [Fact]
    public async Task Cloud_issue_fields_are_mapped_including_rich_text_description()
    {
        var stub = new JiraApiStub().On("/rest/api/3/search/jql", $$"""{"issues":[{{CloudIssue}}],"isLast":true}""");

        var issue = Assert.Single(await CreateClient(stub, Cloud).SearchIssuesAsync("project = ABC").CollectAsync());

        Assert.Equal("10100", issue.Id);
        Assert.Equal("ABC", issue.ProjectKey);
        Assert.Equal("Login fails", issue.Summary);
        Assert.Equal("Bug", issue.IssueType);
        Assert.Equal("In Progress", issue.Status);
        Assert.Equal("indeterminate", issue.StatusCategory);
        Assert.Equal("High", issue.Priority);
        Assert.Equal(new JiraPerson("a1", "Alex Kim", "alex@acme.com", true), issue.Assignee);
        Assert.Equal("r1", issue.Reporter!.Id);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromHours(5.5)), issue.Created);
        Assert.Equal(new DateTimeOffset(2024, 2, 1, 8, 0, 0, TimeSpan.Zero), issue.Updated);
        Assert.Null(issue.ResolutionDate);
        Assert.Equal(["auth", "p1"], issue.Labels);
        Assert.Equal("ABC-0", issue.ParentKey);
        Assert.Equal("Steps:\n1. Open app\n@Alex please check", issue.Description);
        Assert.Equal(5, issue.Fields.GetProperty("customfield_10016").GetInt32()); // custom fields kept
    }

    [Fact]
    public async Task Server_search_uses_start_at_paging_and_plain_text_description()
    {
        var stub = new JiraApiStub()
            .On("/jira/rest/api/2/search", """
                {"startAt":0,"maxResults":2,"total":3,"issues":[
                  {"id":"1","key":"ABC-1","fields":{"summary":"One","description":"h1. Plain *wiki* text","assignee":{"key":"JIRAUSER9","name":"sam","displayName":"Sam"}}},
                  {"id":"2","key":"ABC-2","fields":{"summary":"Two"}}]}
                """, queryContains: "startAt=0")
            .On("/jira/rest/api/2/search", """
                {"startAt":2,"maxResults":2,"total":3,"issues":[{"id":"3","key":"ABC-3","fields":{"summary":"Three"}}]}
                """, queryContains: "startAt=2");

        var issues = await CreateClient(stub, Server).SearchIssuesAsync("project = ABC", ["summary", "description"]).CollectAsync();

        Assert.Equal(["ABC-1", "ABC-2", "ABC-3"], issues.Select(i => i.Key));
        Assert.Equal("h1. Plain *wiki* text", issues[0].Description);
        Assert.Equal("JIRAUSER9", issues[0].Assignee!.Id);
        Assert.Contains("fields=summary,description", stub.RequestedPathsAndQueries.First());
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Offset_paging_continues_after_what_jira_actually_returned()
    {
        // Asked for 100 per page but Jira caps at 2: the next page must start at 2, not 100.
        var stub = new JiraApiStub()
            .On("/rest/api/3/issue/ABC-1/comment", """{"startAt":0,"maxResults":2,"total":5,"comments":[{"id":"1","body":"a"},{"id":"2","body":"b"}]}""", queryContains: "startAt=0")
            .On("/rest/api/3/issue/ABC-1/comment", """{"startAt":2,"maxResults":2,"total":5,"comments":[{"id":"3","body":"c"},{"id":"4","body":"d"}]}""", queryContains: "startAt=2")
            .On("/rest/api/3/issue/ABC-1/comment", """{"startAt":4,"maxResults":2,"total":5,"comments":[{"id":"5","body":"e"}]}""", queryContains: "startAt=4");

        var comments = await CreateClient(stub, Cloud, pageSize: 100).GetCommentsAsync("ABC-1").CollectAsync();

        Assert.Equal(["1", "2", "3", "4", "5"], comments.Select(c => c.Id));
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task Search_can_include_changelog_attachments_and_inline_comments()
    {
        var stub = new JiraApiStub().On("/rest/api/3/search/jql", """
            {"isLast":true,"issues":[{"id":"1","key":"ABC-1","fields":{"summary":"S",
              "comment":{"total":25,"maxResults":1,"startAt":0,"comments":[{"id":"c1","body":"First","author":{"accountId":"a1","displayName":"Alex"}}]},
              "attachment":[{"id":"att1","filename":"screen.png","size":2048,"mimeType":"image/png","created":"2024-01-01T10:00:00.000+0000",
                             "author":{"accountId":"a1","displayName":"Alex"},"content":"https://acme.atlassian.net/rest/api/3/attachment/content/att1"}]},
              "changelog":{"histories":[{"id":"h1","author":{"accountId":"a2","displayName":"Riya"},"created":"2024-01-03T10:00:00.000+0000",
                "items":[{"field":"status","fromString":"To Do","toString":"In Progress"},{"field":"assignee","fromString":null,"toString":"Alex"}]}]}}]}
            """);

        var issue = Assert.Single(await CreateClient(stub, Cloud).SearchIssuesAsync("project = ABC", includeChangelog: true).CollectAsync());

        Assert.Contains("expand=changelog", stub.RequestedPathsAndQueries.Single());
        Assert.Equal("First", Assert.Single(issue.Comments).Body);
        Assert.Equal(25, issue.CommentTotal);
        Assert.True(issue.HasMoreComments);
        var attachment = Assert.Single(issue.Attachments);
        Assert.Equal(("screen.png", 2048L, "image/png"), (attachment.Filename, attachment.Size, attachment.MimeType));
        Assert.Equal(2, issue.Changes.Count);
        Assert.Equal(new JiraChange("h1", new JiraPerson("a2", "Riya", null, true), new DateTimeOffset(2024, 1, 3, 10, 0, 0, TimeSpan.Zero), "status", "To Do", "In Progress"), issue.Changes[0]);
        Assert.Null(issue.Changes[1].From);
    }

    [Fact]
    public async Task Issues_without_comments_attachments_or_changelog_have_empty_lists()
    {
        var stub = new JiraApiStub().On("/rest/api/3/search/jql", """{"isLast":true,"issues":[{"id":"1","key":"ABC-1","fields":{"summary":"S"}}]}""");

        var issue = Assert.Single(await CreateClient(stub, Cloud).SearchIssuesAsync("project = ABC").CollectAsync());

        Assert.Empty(issue.Comments);
        Assert.False(issue.HasMoreComments);
        Assert.Empty(issue.Attachments);
        Assert.Empty(issue.Changes);
        Assert.DoesNotContain("expand", stub.RequestedPathsAndQueries.Single());
    }

    [Fact]
    public async Task Cloud_count_posts_jql_to_approximate_count()
    {
        var stub = new JiraApiStub().On("/rest/api/3/search/approximate-count", """{"count":1234}""");

        var count = await CreateClient(stub, Cloud).CountIssuesAsync("project = ABC");

        Assert.Equal(1234, count);
        var request = stub.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("""{"jql":"project = ABC"}""", stub.Bodies.Single());
    }

    [Fact]
    public async Task Server_count_reads_total_from_an_empty_search_page()
    {
        var stub = new JiraApiStub().On("/jira/rest/api/2/search", """{"startAt":0,"maxResults":0,"total":87,"issues":[]}""");

        var count = await CreateClient(stub, Server).CountIssuesAsync("project = ABC");

        Assert.Equal(87, count);
        Assert.Contains("maxResults=0", stub.RequestedPathsAndQueries.Single());
    }

    [Fact]
    public async Task Count_rejects_empty_jql()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateClient(new JiraApiStub(), Cloud).CountIssuesAsync(""));
    }

    [Fact]
    public async Task Search_rejects_empty_jql()
    {
        var client = CreateClient(new JiraApiStub(), Cloud);

        Assert.Throws<ArgumentException>(() => client.SearchIssuesAsync("  "));
    }

    [Fact]
    public async Task Issue_ids_on_cloud_come_in_big_token_pages_with_only_the_id_field()
    {
        var stub = new JiraApiStub()
            .On("/rest/api/3/search/jql", """{"issues":[{"id":"1"},{"id":"2"}],"nextPageToken":"t2","isLast":false}""")
            .On("/rest/api/3/search/jql", """{"issues":[{"id":"3"}],"isLast":true}""", queryContains: "nextPageToken=t2");

        var ids = await CreateClient(stub, Cloud).GetIssueIdsAsync("project = ABC").CollectAsync();

        Assert.Equal(["1", "2", "3"], ids);
        Assert.Contains("fields=id", stub.RequestedPathsAndQueries.First());
        Assert.Contains("maxResults=5000", stub.RequestedPathsAndQueries.First());
    }

    [Fact]
    public async Task Issue_ids_on_server_page_with_start_at()
    {
        var stub = new JiraApiStub()
            .On("/jira/rest/api/2/search", """{"startAt":0,"total":3,"issues":[{"id":"1"},{"id":"2"}]}""", queryContains: "startAt=0")
            .On("/jira/rest/api/2/search", """{"startAt":2,"total":3,"issues":[{"id":"3"}]}""", queryContains: "startAt=2");

        var ids = await CreateClient(stub, Server).GetIssueIdsAsync("project = ABC").CollectAsync();

        Assert.Equal(["1", "2", "3"], ids); // Server returned fewer than asked for; paging carried on after what came back
    }

    // ---- Single issue, comments, worklogs ----

    [Fact]
    public async Task Get_issue_returns_the_mapped_issue()
    {
        var stub = new JiraApiStub().On("/rest/api/3/issue/ABC-1", CloudIssue);

        var issue = await CreateClient(stub, Cloud).GetIssueAsync("ABC-1");

        Assert.Equal("Login fails", issue.Summary);
        Assert.Contains("fields=*all", stub.RequestedPathsAndQueries.Single());
    }

    [Fact]
    public async Task Missing_issue_throws_with_jira_error_message()
    {
        var stub = new JiraApiStub().On("/rest/api/3/issue/NOPE-1",
            """{"errorMessages":["Issue does not exist or you do not have permission to see it."],"errors":{}}""",
            status: HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<JiraApiException>(() => CreateClient(stub, Cloud).GetIssueAsync("NOPE-1"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("Not found in Jira: Issue does not exist or you do not have permission to see it.", ex.Message);
        Assert.Single(ex.JiraErrors);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Jira rejected the credentials.")]
    [InlineData(HttpStatusCode.Forbidden, "This account isn't allowed to read that in Jira.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Jira is rate limiting requests.")]
    [InlineData(HttpStatusCode.BadGateway, "Jira returned 502 Bad Gateway.")]
    public async Task Error_responses_without_a_jira_body_get_a_readable_message(HttpStatusCode status, string expected)
    {
        var stub = new JiraApiStub().On("/rest/api/3/field", () => new HttpResponseMessage(status) { Content = new StringContent("<html>oops</html>") });

        var ex = await Assert.ThrowsAsync<JiraApiException>(() => CreateClient(stub, Cloud).GetFieldsAsync());

        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public async Task Field_errors_are_included_in_the_message()
    {
        var stub = new JiraApiStub().On("/rest/api/3/search/jql",
            """{"errorMessages":[],"errors":{"jql":"Field 'foo' does not exist."}}""", status: HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<JiraApiException>(() => CreateClient(stub, Cloud).SearchIssuesAsync("foo = 1").CollectAsync());

        Assert.Contains("jql: Field 'foo' does not exist.", ex.Message);
    }

    [Fact]
    public async Task Comments_convert_rich_text_bodies()
    {
        var stub = new JiraApiStub().On("/rest/api/3/issue/ABC-1/comment", """
            {"startAt":0,"maxResults":50,"total":1,"comments":[{"id":"7","author":{"accountId":"a1","displayName":"Alex"},
             "body":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Looks good"}]}]},
             "created":"2024-03-01T09:00:00.000+0000","updated":"2024-03-02T09:00:00.000+0000"}]}
            """);

        var comment = Assert.Single(await CreateClient(stub, Cloud).GetCommentsAsync("ABC-1").CollectAsync());

        Assert.Equal("Looks good", comment.Body);
        Assert.Equal("Alex", comment.Author!.DisplayName);
        Assert.Equal(new DateTimeOffset(2024, 3, 2, 9, 0, 0, TimeSpan.Zero), comment.Updated);
    }

    [Fact]
    public async Task Worklogs_are_mapped()
    {
        var stub = new JiraApiStub().On("/jira/rest/api/2/issue/ABC-1/worklog", """
            {"startAt":0,"maxResults":20,"total":1,"worklogs":[{"id":"9","author":{"name":"sam","displayName":"Sam"},
             "timeSpentSeconds":5400,"started":"2024-03-01T09:00:00.000+0000","comment":"Pairing"}]}
            """);

        var worklog = Assert.Single(await CreateClient(stub, Server).GetWorklogsAsync("ABC-1").CollectAsync());

        Assert.Equal(5400, worklog.TimeSpentSeconds);
        Assert.Equal("sam", worklog.Author!.Id);
        Assert.Equal("Pairing", worklog.Comment);
    }

    // ---- Users ----

    [Fact]
    public async Task Cloud_users_page_until_an_empty_page()
    {
        var stub = new JiraApiStub()
            .On("/rest/api/3/users/search", """[{"accountId":"a1","displayName":"Alex","active":true},{"accountId":"a2","displayName":"Bot","active":false}]""", queryContains: "startAt=0")
            .On("/rest/api/3/users/search", """[{"accountId":"a3","displayName":"Riya"}]""", queryContains: "startAt=2")
            .On("/rest/api/3/users/search", "[]", queryContains: "startAt=3");

        var users = await CreateClient(stub, Cloud).GetUsersAsync().CollectAsync();

        Assert.Equal(["a1", "a2", "a3"], users.Select(u => u.Id));
        Assert.False(users[1].Active);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task Server_users_search_with_a_dot_to_match_everyone()
    {
        var stub = new JiraApiStub()
            .On("/jira/rest/api/2/user/search", """[{"key":"JIRAUSER1","name":"alex","displayName":"Alex"}]""", queryContains: "startAt=0")
            .On("/jira/rest/api/2/user/search", "[]", queryContains: "startAt=1");

        var users = await CreateClient(stub, Server).GetUsersAsync().CollectAsync();

        Assert.Equal("JIRAUSER1", Assert.Single(users).Id);
        Assert.Contains("username=.&startAt=0", stub.RequestedPathsAndQueries.First());
    }

    // ---- Boards and sprints ----

    [Fact]
    public async Task Boards_page_through_the_agile_api_and_can_filter_by_project()
    {
        var stub = new JiraApiStub()
            .On("/rest/agile/1.0/board", """{"maxResults":2,"startAt":0,"isLast":false,"values":[{"id":1,"name":"ABC board","type":"scrum","location":{"projectKey":"ABC"}},{"id":2,"name":"Kanban","type":"kanban"}]}""", queryContains: "startAt=0")
            .On("/rest/agile/1.0/board", """{"maxResults":2,"startAt":2,"isLast":true,"values":[{"id":3,"name":"Third","type":"scrum"}]}""", queryContains: "startAt=2");

        var boards = await CreateClient(stub, Cloud).GetBoardsAsync("ABC").CollectAsync();

        Assert.Equal([1L, 2L, 3L], boards.Select(b => b.Id));
        Assert.Equal("ABC", boards[0].ProjectKey);
        Assert.All(stub.RequestedPathsAndQueries, q => Assert.Contains("projectKeyOrId=ABC&startAt=", q));
    }

    [Fact]
    public async Task Sprints_are_mapped_with_iso_dates()
    {
        var stub = new JiraApiStub().On("/rest/agile/1.0/board/1/sprint", """
            {"maxResults":50,"startAt":0,"isLast":true,"values":[{"id":11,"name":"Sprint 1","state":"closed",
             "startDate":"2024-01-01T09:00:00.000Z","endDate":"2024-01-14T17:00:00.000Z","completeDate":"2024-01-14T18:00:00.000Z",
             "goal":"Ship login","originBoardId":1},
             {"id":12,"name":"Sprint 2","state":"active"}]}
            """);

        var sprints = await CreateClient(stub, Cloud).GetSprintsAsync(1).CollectAsync();

        Assert.Equal(2, sprints.Count);
        Assert.Equal(new DateTimeOffset(2024, 1, 14, 17, 0, 0, TimeSpan.Zero), sprints[0].EndDate);
        Assert.Equal("Ship login", sprints[0].Goal);
        Assert.Equal(1, sprints[1].BoardId);
        Assert.Null(sprints[1].StartDate);
    }

    [Fact]
    public async Task Kanban_board_sprints_are_empty_instead_of_an_error()
    {
        var stub = new JiraApiStub().On("/rest/agile/1.0/board/2/sprint",
            """{"errorMessages":["The board does not support sprints"],"errors":{}}""", status: HttpStatusCode.BadRequest);

        Assert.Empty(await CreateClient(stub, Cloud).GetSprintsAsync(2).CollectAsync());
    }

    [Fact]
    public async Task Other_sprint_errors_still_throw()
    {
        var stub = new JiraApiStub().On("/rest/agile/1.0/board/2/sprint", """{"errorMessages":["Board not found"]}""", status: HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<JiraApiException>(() => CreateClient(stub, Cloud).GetSprintsAsync(2).CollectAsync());
    }

    // ---- Fields ----

    [Fact]
    public async Task Fields_are_mapped()
    {
        var stub = new JiraApiStub().On("/rest/api/3/field", """
            [{"id":"summary","name":"Summary","custom":false,"schema":{"type":"string"}},
             {"id":"customfield_10016","name":"Story Points","custom":true,"schema":{"type":"number"}}]
            """);

        var fields = await CreateClient(stub, Cloud).GetFieldsAsync();

        Assert.Equal(new JiraField("customfield_10016", "Story Points", true, "number"), fields[1]);
    }

    // ---- Cancellation and retries ----

    [Fact]
    public async Task Stopping_early_does_not_fetch_more_pages()
    {
        var stub = new JiraApiStub().On("/rest/api/3/project/search",
            """{"isLast":false,"values":[{"id":"1","key":"A","name":"A"},{"id":"2","key":"B","name":"B"}]}""");

        await foreach (var _ in CreateClient(stub, Cloud).GetProjectsAsync())
        {
            break;
        }

        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Cancellation_stops_paging()
    {
        using var cts = new CancellationTokenSource();
        var stub = new JiraApiStub().On("/rest/api/3/project/search",
            """{"isLast":false,"values":[{"id":"1","key":"A","name":"A"}]}""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in CreateClient(stub, Cloud).GetProjectsAsync(cts.Token))
            {
                cts.Cancel();
            }
        });
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Rate_limited_page_is_retried_through_the_retry_handler()
    {
        var calls = 0;
        var stub = new JiraApiStub().On("/rest/api/3/field", () => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(3)) } }
            : JiraApiStub.Json("""[{"id":"summary","name":"Summary"}]"""));
        var delays = new List<TimeSpan>();
        var retry = new JiraRetryHandler(NullLogger<JiraRetryHandler>.Instance)
        {
            InnerHandler = stub,
            Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; },
        };

        var fields = await new JiraClient(new HttpClient(retry), Cloud, NullLogger<JiraClient>.Instance).GetFieldsAsync();

        Assert.Single(fields);
        Assert.Equal([TimeSpan.FromSeconds(3)], delays);
    }
    // ---- Changes ----

    private static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

    private static System.Text.Json.JsonElement Body(JiraApiStub stub, int index = 0) =>
        System.Text.Json.JsonDocument.Parse(stub.Bodies[index]!).RootElement;

    [Fact]
    public async Task Cloud_comment_is_sent_as_a_rich_text_document()
    {
        var stub = new JiraApiStub().On("/rest/api/3/issue/ABC-1/comment", """
            {"id":"500","author":{"accountId":"a1","displayName":"Alex"},"body":{"type":"doc","version":1,"content":[]},"created":"2026-10-31T09:00:00.000+0000"}
            """, status: HttpStatusCode.Created);

        var comment = await CreateClient(stub, Cloud).AddCommentAsync("ABC-1", "First line\r\nsecond line\n\nNew paragraph");

        Assert.Equal("500", comment.Id);
        Assert.Equal(HttpMethod.Post, stub.Requests.Single().Method);
        Assert.Equal("""
            {"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"First line"},{"type":"hardBreak"},{"type":"text","text":"second line"}]},{"type":"paragraph","content":[{"type":"text","text":"New paragraph"}]}]}
            """, Body(stub).GetProperty("body").GetRawText());
    }

    [Fact]
    public async Task Server_comment_is_sent_as_plain_text()
    {
        var stub = new JiraApiStub().On("/jira/rest/api/2/issue/ABC-1/comment", """{"id":"7","body":"Hi\nthere"}""", status: HttpStatusCode.Created);

        await CreateClient(stub, Server).AddCommentAsync("ABC-1", "Hi\r\nthere");

        Assert.Equal("Hi\nthere", Body(stub).GetProperty("body").GetString());
    }

    [Fact]
    public async Task Transitions_list_the_target_status_and_moving_posts_the_transition_id()
    {
        var stub = new JiraApiStub()
            .On("/rest/api/3/issue/ABC-1/transitions", """
                {"transitions":[{"id":"21","name":"Start progress","to":{"name":"In Progress","statusCategory":{"key":"indeterminate"}}},
                                {"id":"31","name":"Done","to":{"name":"Done","statusCategory":{"key":"done"}}}]}
                """);
        var client = CreateClient(stub, Cloud);

        var transitions = await client.GetTransitionsAsync("ABC-1");
        stub.On("/rest/api/3/issue/ABC-1/transitions", NoContent);
        await client.TransitionIssueAsync("ABC-1", "21");

        Assert.Equal(new JiraTransition("21", "Start progress", "In Progress", "indeterminate"), transitions[0]);
        Assert.Equal(2, transitions.Count);
        Assert.Equal(HttpMethod.Post, stub.Requests[1].Method);
        Assert.Equal("""{"transition":{"id":"21"}}""", stub.Bodies[1]);
    }

    [Fact]
    public async Task Update_sends_only_the_changed_fields()
    {
        var stub = new JiraApiStub().On("/rest/api/3/issue/ABC-1", NoContent);
        var client = CreateClient(stub, Cloud);

        await client.UpdateIssueAsync("ABC-1", new JiraIssueUpdate(Summary: "New title", Priority: "High"));
        await client.UpdateIssueAsync("ABC-1", new JiraIssueUpdate(Description: ""));
        await client.UpdateIssueAsync("ABC-1", new JiraIssueUpdate());

        Assert.Equal(HttpMethod.Put, stub.Requests[0].Method);
        Assert.Equal("""{"fields":{"summary":"New title","priority":{"name":"High"}}}""", stub.Bodies[0]);
        Assert.Equal("""{"fields":{"description":null}}""", stub.Bodies[1]); // empty clears it
        Assert.Equal(2, stub.Requests.Count); // nothing to change, nothing sent
    }

    [Fact]
    public async Task Assigning_uses_the_account_id_on_cloud_and_the_username_on_server()
    {
        var cloud = new JiraApiStub().On("/rest/api/3/issue/ABC-1/assignee", NoContent);
        var server = new JiraApiStub().On("/jira/rest/api/2/issue/ABC-1/assignee", NoContent);

        await CreateClient(cloud, Cloud).AssignIssueAsync("ABC-1", "acc-1");
        await CreateClient(cloud, Cloud).AssignIssueAsync("ABC-1", null);
        await CreateClient(server, Server).AssignIssueAsync("ABC-1", "alex");

        Assert.Equal(["""{"accountId":"acc-1"}""", """{"accountId":null}"""], cloud.Bodies);
        Assert.Equal("""{"name":"alex"}""", server.Bodies.Single());
    }

    [Fact]
    public async Task Assignable_people_on_server_carry_the_username_the_assign_call_needs()
    {
        var stub = new JiraApiStub().On("/jira/rest/api/2/user/assignable/search",
            """[{"key":"JIRAUSER10100","name":"alex","displayName":"Alex Kim","active":true}]""");

        var people = await CreateClient(stub, Server).GetAssignableUsersAsync("ABC-1", "al");

        Assert.Equal(new JiraPerson("alex", "Alex Kim", null, true), people.Single());
        Assert.Contains("issueKey=ABC-1&username=al&maxResults=50", stub.RequestedPathsAndQueries.Single());
    }

    [Fact]
    public async Task Assignable_people_on_cloud_are_searched_by_query()
    {
        var stub = new JiraApiStub().On("/rest/api/3/user/assignable/search",
            """[{"accountId":"acc-1","displayName":"Alex Kim","active":true}]""");

        var people = await CreateClient(stub, Cloud).GetAssignableUsersAsync("ABC-1", "alex kim");

        Assert.Equal("acc-1", people.Single().Id);
        Assert.Contains("query=alex kim", stub.RequestedPathsAndQueries.Single());
    }

    [Fact]
    public async Task Priorities_are_listed_by_name()
    {
        var stub = new JiraApiStub().On("/rest/api/3/priority", """[{"id":"1","name":"Highest"},{"id":"3","name":"Medium"}]""");

        Assert.Equal(["Highest", "Medium"], await CreateClient(stub, Cloud).GetPrioritiesAsync());
    }

    [Fact]
    public async Task Refused_change_says_the_account_cant_make_it_and_keeps_jiras_reasons()
    {
        var stub = new JiraApiStub()
            .On("/rest/api/3/issue/ABC-1/assignee", """{"errorMessages":["You do not have permission to assign issues."]}""", status: HttpStatusCode.Forbidden)
            .On("/rest/api/3/issue/ABC-1", """{"errorMessages":[],"errors":{"summary":"Summary must be less than 255 characters."}}""", status: HttpStatusCode.BadRequest);
        var client = CreateClient(stub, Cloud);

        var forbidden = await Assert.ThrowsAsync<JiraApiException>(() => client.AssignIssueAsync("ABC-1", "acc-1"));
        var invalid = await Assert.ThrowsAsync<JiraApiException>(() => client.UpdateIssueAsync("ABC-1", new JiraIssueUpdate(Summary: "x")));

        Assert.Equal("This account isn't allowed to make that change in Jira: You do not have permission to assign issues.", forbidden.Message);
        Assert.Equal(["summary: Summary must be less than 255 characters."], invalid.JiraErrors);
    }

    [Fact]
    public async Task Changes_are_never_retried_so_a_comment_cant_be_posted_twice()
    {
        var stub = new JiraApiStub().On("/rest/api/3/issue/ABC-1/comment", () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var retry = new JiraRetryHandler(NullLogger<JiraRetryHandler>.Instance) { InnerHandler = stub, Delay = (_, _) => Task.CompletedTask };

        await Assert.ThrowsAsync<JiraApiException>(() =>
            new JiraClient(new HttpClient(retry), Cloud, NullLogger<JiraClient>.Instance).AddCommentAsync("ABC-1", "Hi"));

        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Get_issue_includes_its_history()
    {
        var stub = new JiraApiStub().On("/rest/api/3/issue/ABC-1", CloudIssue);

        await CreateClient(stub, Cloud).GetIssueAsync("ABC-1");

        Assert.Contains("expand=changelog", stub.RequestedPathsAndQueries.Single());
    }
}

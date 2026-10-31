using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AICollaborationPlatform.App.Models;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.App.Services;

/// <summary>
/// Jira REST client for one connection. Cloud uses REST API v3, Server / Data Center v2;
/// boards and sprints come from the Agile API, which is the same on both.
/// </summary>
public sealed class JiraClient(HttpClient http, JiraConnectionSettings settings, ILogger<JiraClient> logger, int pageSize = 100)
    : IJiraClient
{
    /// <summary>Name of the HttpClient registered with the retry handler.</summary>
    public const string HttpClientName = "JiraApi";

    private bool IsCloud => settings.AuthType == JiraAuthType.Cloud;

    private string Api => $"rest/api/{JiraAuth.ApiVersion(settings)}";

    public IAsyncEnumerable<JiraProject> GetProjectsAsync(CancellationToken cancellationToken = default) =>
        IsCloud
            ? GetOffsetPagedAsync($"{Api}/project/search", "values", MapProject, cancellationToken)
            : GetArrayAsync($"{Api}/project", MapProject, cancellationToken); // Server returns every project at once

    public IAsyncEnumerable<JiraIssue> SearchIssuesAsync(
        string jql, IEnumerable<string>? fields = null, bool includeChangelog = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jql);
        var query = $"jql={Uri.EscapeDataString(jql)}&fields={Uri.EscapeDataString(string.Join(',', fields ?? ["*all"]))}"
            + (includeChangelog ? "&expand=changelog" : "");

        // Cloud's search pages with a token; Server's with startAt/total.
        return IsCloud
            ? GetTokenPagedAsync($"{Api}/search/jql?{query}", "issues", MapIssue, cancellationToken)
            : GetOffsetPagedAsync($"{Api}/search?{query}", "issues", MapIssue, cancellationToken);
    }

    public async Task<long> CountIssuesAsync(string jql, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jql);

        if (IsCloud)
        {
            // Cloud's token-paged search has no total; this endpoint gives a quick estimate.
            using var request = JiraAuth.CreateRequest(settings, HttpMethod.Post, $"{Api}/search/approximate-count");
            request.Content = JsonContent.Create(new { jql });
            using var json = await SendForJsonAsync(request, cancellationToken);
            return JiraJson.GetLong(json.RootElement, "count") ?? 0;
        }

        using var page = await GetJsonAsync($"{Api}/search?jql={Uri.EscapeDataString(jql)}&maxResults=0", cancellationToken);
        return JiraJson.GetLong(page.RootElement, "total") ?? 0;
    }

    public async Task<JiraIssue> GetIssueAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        using var json = await GetJsonAsync($"{Api}/issue/{Uri.EscapeDataString(issueKey)}?fields=*all", cancellationToken);
        return MapIssue(json.RootElement);
    }

    public IAsyncEnumerable<JiraComment> GetCommentsAsync(string issueKey, CancellationToken cancellationToken = default) =>
        GetOffsetPagedAsync($"{Api}/issue/{Uri.EscapeDataString(issueKey)}/comment", "comments", MapComment, cancellationToken);

    public IAsyncEnumerable<JiraWorklog> GetWorklogsAsync(string issueKey, CancellationToken cancellationToken = default) =>
        GetOffsetPagedAsync($"{Api}/issue/{Uri.EscapeDataString(issueKey)}/worklog", "worklogs", MapWorklog, cancellationToken);

    public IAsyncEnumerable<JiraPerson> GetUsersAsync(CancellationToken cancellationToken = default) =>
        // Server needs a search term; "." matches every user.
        GetOffsetPagedAsync(IsCloud ? $"{Api}/users/search" : $"{Api}/user/search?username=.", null, MapPerson, cancellationToken);

    public IAsyncEnumerable<JiraBoard> GetBoardsAsync(string? projectKey = null, CancellationToken cancellationToken = default) =>
        GetOffsetPagedAsync(
            projectKey is null ? "rest/agile/1.0/board" : $"rest/agile/1.0/board?projectKeyOrId={Uri.EscapeDataString(projectKey)}",
            "values", MapBoard, cancellationToken);

    public async IAsyncEnumerable<JiraSprint> GetSprintsAsync(long boardId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sprints = GetOffsetPagedAsync($"rest/agile/1.0/board/{boardId}/sprint", "values", s => MapSprint(s, boardId), cancellationToken);
        await using var enumerator = sprints.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    yield break;
                }
            }
            catch (JiraApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && ex.Message.Contains("sprint", StringComparison.OrdinalIgnoreCase))
            {
                // "The board does not support sprints": a Kanban board.
                yield break;
            }

            yield return enumerator.Current;
        }
    }

    public async Task<IReadOnlyList<JiraField>> GetFieldsAsync(CancellationToken cancellationToken = default)
    {
        var fields = new List<JiraField>();
        await foreach (var field in GetArrayAsync($"{Api}/field", MapField, cancellationToken))
        {
            fields.Add(field);
        }

        return fields;
    }

    // ---- Paging ----

    /// <summary>
    /// startAt/maxResults paging. Stops on isLast, on reaching total, or on an empty page.
    /// Jira may return fewer items than asked for, so the next page always starts after what actually came back.
    /// <paramref name="itemsProperty"/> null means the response is a bare JSON array.
    /// </summary>
    private async IAsyncEnumerable<T> GetOffsetPagedAsync<T>(
        string path, string? itemsProperty, Func<JsonElement, T> map, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var separator = path.Contains('?') ? '&' : '?';
        var startAt = 0;

        while (true)
        {
            using var json = await GetJsonAsync($"{path}{separator}startAt={startAt}&maxResults={pageSize}", cancellationToken);
            var root = json.RootElement;
            var items = itemsProperty is null ? root : JiraJson.Get(root, itemsProperty);
            var count = 0;

            if (items is { ValueKind: JsonValueKind.Array } array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    count++;
                    yield return map(item);
                }
            }

            startAt += count;
            var isLast = JiraJson.GetBool(root, "isLast");
            var total = JiraJson.GetLong(root, "total");

            if (count == 0 || isLast == true || (isLast is null && total is { } t && startAt >= t))
            {
                yield break;
            }
        }
    }

    /// <summary>nextPageToken paging, used by Jira Cloud's issue search.</summary>
    private async IAsyncEnumerable<T> GetTokenPagedAsync<T>(
        string path, string itemsProperty, Func<JsonElement, T> map, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? token = null;

        do
        {
            var page = $"{path}&maxResults={pageSize}" + (token is null ? "" : $"&nextPageToken={Uri.EscapeDataString(token)}");
            using var json = await GetJsonAsync(page, cancellationToken);
            var root = json.RootElement;

            if (JiraJson.Get(root, itemsProperty) is { ValueKind: JsonValueKind.Array } items)
            {
                foreach (var item in items.EnumerateArray())
                {
                    yield return map(item);
                }
            }

            token = JiraJson.GetBool(root, "isLast") == true ? null : JiraJson.GetString(root, "nextPageToken");
        }
        while (token is not null);
    }

    /// <summary>Endpoints that return everything as one JSON array.</summary>
    private async IAsyncEnumerable<T> GetArrayAsync<T>(string path, Func<JsonElement, T> map, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(path, cancellationToken);
        if (json.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in json.RootElement.EnumerateArray())
            {
                yield return map(item);
            }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var request = JiraAuth.CreateRequest(settings, HttpMethod.Get, pathAndQuery);
        return await SendForJsonAsync(request, cancellationToken);
    }

    private async Task<JsonDocument> SendForJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, cancellationToken);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task<JiraApiException> CreateErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        try
        {
            // Jira errors look like {"errorMessages":["..."],"errors":{"field":"..."}}.
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (JiraJson.Get(json.RootElement, "errorMessages") is { ValueKind: JsonValueKind.Array } messages)
            {
                errors.AddRange(messages.EnumerateArray().Select(m => m.GetString()).OfType<string>());
            }

            if (JiraJson.Get(json.RootElement, "errors") is { ValueKind: JsonValueKind.Object } fieldErrors)
            {
                errors.AddRange(fieldErrors.EnumerateObject().Select(e => $"{e.Name}: {e.Value}"));
            }
        }
        catch (JsonException)
        {
        }

        var status = (int)response.StatusCode;
        var reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Jira rejected the credentials",
            HttpStatusCode.Forbidden => "This account isn't allowed to read that in Jira",
            HttpStatusCode.NotFound => "Not found in Jira",
            HttpStatusCode.TooManyRequests => "Jira is rate limiting requests",
            _ => $"Jira returned {status} {response.ReasonPhrase}",
        };
        var message = errors.Count > 0 ? $"{reason}: {string.Join(" ", errors)}" : $"{reason}.";

        logger.LogWarning("Jira request {Path} failed with {Status}: {Errors}", response.RequestMessage?.RequestUri?.AbsolutePath, status, errors);
        return new JiraApiException(response.StatusCode, message, errors);
    }

    // ---- Mapping JSON to models ----

    private static JiraProject MapProject(JsonElement p) => new(
        JiraJson.GetString(p, "id") ?? "",
        JiraJson.GetString(p, "key") ?? "",
        JiraJson.GetString(p, "name") ?? "",
        JiraJson.GetString(p, "projectTypeKey"),
        JiraJson.GetPerson(p, "lead"));

    private static JiraIssue MapIssue(JsonElement issue) => new(
        JiraJson.GetString(issue, "id") ?? "",
        JiraJson.GetString(issue, "key") ?? "",
        JiraJson.GetString(issue, "fields", "project", "key"),
        JiraJson.GetString(issue, "fields", "summary") ?? "",
        JiraJson.GetString(issue, "fields", "issuetype", "name"),
        JiraJson.GetString(issue, "fields", "status", "name"),
        JiraJson.GetString(issue, "fields", "status", "statusCategory", "key"),
        JiraJson.GetString(issue, "fields", "priority", "name"),
        JiraJson.GetPerson(issue, "fields", "assignee"),
        JiraJson.GetPerson(issue, "fields", "reporter"),
        JiraJson.GetDate(issue, "fields", "created"),
        JiraJson.GetDate(issue, "fields", "updated"),
        JiraJson.GetDate(issue, "fields", "resolutiondate"),
        JiraJson.GetText(issue, "fields", "description"),
        JiraJson.Get(issue, "fields", "labels") is { ValueKind: JsonValueKind.Array } labels
            ? labels.EnumerateArray().Select(l => l.GetString()).OfType<string>().ToList()
            : [],
        JiraJson.GetString(issue, "fields", "parent", "key"),
        // Clone so the fields outlive the page's JsonDocument, which is disposed after paging on.
        JiraJson.Get(issue, "fields")?.Clone() ?? default,
        MapArray(issue, MapComment, "fields", "comment", "comments"),
        (int)(JiraJson.GetLong(issue, "fields", "comment", "total") ?? 0),
        MapArray(issue, MapAttachment, "fields", "attachment"),
        MapChanges(issue));

    private static IReadOnlyList<T> MapArray<T>(JsonElement element, Func<JsonElement, T> map, params string[] path) =>
        JiraJson.Get(element, path) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().Select(map).ToList() : [];

    private static JiraAttachment MapAttachment(JsonElement a) => new(
        JiraJson.GetString(a, "id") ?? "",
        JiraJson.GetString(a, "filename") ?? "",
        JiraJson.GetLong(a, "size") ?? 0,
        JiraJson.GetString(a, "mimeType"),
        JiraJson.GetDate(a, "created"),
        JiraJson.GetPerson(a, "author"),
        JiraJson.GetString(a, "content"));

    /// <summary>Flattens changelog.histories[].items[] into one row per changed field.</summary>
    private static IReadOnlyList<JiraChange> MapChanges(JsonElement issue)
    {
        if (JiraJson.Get(issue, "changelog", "histories") is not { ValueKind: JsonValueKind.Array } histories)
        {
            return [];
        }

        var changes = new List<JiraChange>();
        foreach (var history in histories.EnumerateArray())
        {
            var id = JiraJson.GetString(history, "id") ?? "";
            var author = JiraJson.GetPerson(history, "author");
            var created = JiraJson.GetDate(history, "created");

            if (JiraJson.Get(history, "items") is { ValueKind: JsonValueKind.Array } items)
            {
                changes.AddRange(items.EnumerateArray().Select(item => new JiraChange(
                    id, author, created,
                    JiraJson.GetString(item, "field") ?? "",
                    JiraJson.GetString(item, "fromString"),
                    JiraJson.GetString(item, "toString"))));
            }
        }

        return changes;
    }

    private static JiraComment MapComment(JsonElement c) => new(
        JiraJson.GetString(c, "id") ?? "",
        JiraJson.GetPerson(c, "author"),
        JiraJson.GetText(c, "body") ?? "",
        JiraJson.GetDate(c, "created"),
        JiraJson.GetDate(c, "updated"));

    private static JiraWorklog MapWorklog(JsonElement w) => new(
        JiraJson.GetString(w, "id") ?? "",
        JiraJson.GetPerson(w, "author"),
        JiraJson.GetLong(w, "timeSpentSeconds") ?? 0,
        JiraJson.GetDate(w, "started"),
        JiraJson.GetText(w, "comment"));

    private static JiraPerson MapPerson(JsonElement u) =>
        JiraJson.GetPerson(u) ?? new JiraPerson("", JiraJson.GetString(u, "displayName") ?? "", null, false);

    private static JiraBoard MapBoard(JsonElement b) => new(
        JiraJson.GetLong(b, "id") ?? 0,
        JiraJson.GetString(b, "name") ?? "",
        JiraJson.GetString(b, "type"),
        JiraJson.GetString(b, "location", "projectKey"));

    private static JiraSprint MapSprint(JsonElement s, long boardId) => new(
        JiraJson.GetLong(s, "id") ?? 0,
        JiraJson.GetString(s, "name") ?? "",
        JiraJson.GetString(s, "state"),
        JiraJson.GetDate(s, "startDate"),
        JiraJson.GetDate(s, "endDate"),
        JiraJson.GetDate(s, "completeDate"),
        JiraJson.GetString(s, "goal"),
        JiraJson.GetLong(s, "originBoardId") ?? boardId);

    private static JiraField MapField(JsonElement f) => new(
        JiraJson.GetString(f, "id") ?? "",
        JiraJson.GetString(f, "name") ?? "",
        JiraJson.GetBool(f, "custom") ?? false,
        JiraJson.GetString(f, "schema", "type"));
}

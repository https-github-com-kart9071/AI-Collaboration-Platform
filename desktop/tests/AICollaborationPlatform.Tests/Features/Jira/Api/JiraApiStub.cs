using System.Net;
using System.Net.Http;
using System.Text;

namespace AICollaborationPlatform.Tests;

/// <summary>
/// A fake Jira server. Register responses by path (and optionally a query fragment);
/// every request is recorded so tests can check exactly what was asked for.
/// </summary>
internal sealed class JiraApiStub : HttpMessageHandler
{
    private readonly List<(string Path, string? QueryContains, Func<HttpResponseMessage> Respond)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Request bodies, captured on arrival because the client disposes requests afterwards.</summary>
    public List<string?> Bodies { get; } = [];

    public JiraApiStub On(string path, string json, string? queryContains = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((path, queryContains, () => Json(json, status)));
        return this;
    }

    public JiraApiStub On(string path, Func<HttpResponseMessage> respond, string? queryContains = null)
    {
        _routes.Add((path, queryContains, respond));
        return this;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public IEnumerable<string> RequestedPathsAndQueries => Requests.Select(r => Uri.UnescapeDataString(r.RequestUri!.PathAndQuery));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        Bodies.Add(request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());

        var path = request.RequestUri!.AbsolutePath;
        var query = Uri.UnescapeDataString(request.RequestUri.Query);

        // Last matching registration wins, so specific routes can be added after general ones.
        for (var i = _routes.Count - 1; i >= 0; i--)
        {
            var route = _routes[i];
            if (path == route.Path && (route.QueryContains is null || query.Contains(route.QueryContains)))
            {
                return Task.FromResult(route.Respond());
            }
        }

        return Task.FromResult(Json($$"""{"errorMessages":["No stub for {{path}}{{query}}"]}""", HttpStatusCode.NotFound));
    }
}

internal static class AsyncEnumerableTestExtensions
{
    public static async Task<List<T>> CollectAsync<T>(this IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
    }
}

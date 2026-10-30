using System.Net;
using System.Net.Http;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICollaborationPlatform.Tests;

internal sealed class FakeDialogService : IDialogService
{
    public int JiraDialogShownCount { get; private set; }

    public bool ShowJiraConnectionDialog()
    {
        JiraDialogShownCount++;
        return false;
    }
}

/// <summary>Answers every request with the given function and remembers the last request.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public static StubHttpHandler Returning(HttpStatusCode status, string body = "", string mediaType = "application/json") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType),
        }));

    public static StubHttpHandler Throwing(Exception exception) =>
        new((_, _) => Task.FromException<HttpResponseMessage>(exception));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return respond(request, cancellationToken);
    }
}

internal sealed class FakeJiraConnectionService(Func<JiraConnectionSettings, CancellationToken, Task<JiraConnectionResult>> respond) : IJiraConnectionService
{
    public int CallCount { get; private set; }

    public JiraConnectionSettings? LastSettings { get; private set; }

    public static FakeJiraConnectionService Returning(JiraConnectionResult result) =>
        new((_, _) => Task.FromResult(result));

    public Task<JiraConnectionResult> TestConnectionAsync(JiraConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastSettings = settings;
        return respond(settings, cancellationToken);
    }
}

internal static class TestSession
{
    public static JiraSession Create() => new(NullLogger<JiraSession>.Instance);
}

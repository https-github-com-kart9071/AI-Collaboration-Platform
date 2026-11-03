using System.Net;
using System.Net.Http;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICollaborationPlatform.Tests;

internal sealed class FakeDialogService : IDialogService
{
    public int JiraDialogShownCount { get; private set; }

    public bool ConfirmAnswer { get; set; } = true;

    public int ConfirmCount { get; private set; }

    public bool ShowJiraConnectionDialog()
    {
        JiraDialogShownCount++;
        return false;
    }

    public bool Confirm(string title, string message)
    {
        ConfirmCount++;
        return ConfirmAnswer;
    }
}

internal sealed class InMemoryCredentialStore : ICredentialStore
{
    public Dictionary<string, (string UserName, string Secret)> Entries { get; } = [];

    public Exception? FailWith { get; set; }

    public string? Read(string target)
    {
        if (FailWith is not null) throw FailWith;
        return Entries.TryGetValue(target, out var entry) ? entry.Secret : null;
    }

    public void Write(string target, string userName, string secret)
    {
        if (FailWith is not null) throw FailWith;
        Entries[target] = (userName, secret);
    }

    public bool Delete(string target)
    {
        if (FailWith is not null) throw FailWith;
        return Entries.Remove(target);
    }
}

internal sealed class InMemorySettingsStore : ISettingsStore
{
    public AppSettings Settings { get; set; } = new();

    public int SaveCount { get; private set; }

    public AppSettings Load() => Settings;

    public void Save(AppSettings settings)
    {
        Settings = settings;
        SaveCount++;
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

internal sealed class FakeSyncService : IJiraSyncService
{
    public JiraDataSummary Summary { get; set; } = new(0, 0, 0, 0, 0, 0, null, null);

    public List<JiraProject> Projects { get; } = [];

    public Func<JiraSyncOptions, IProgress<JiraSyncProgress>?, CancellationToken, Task<JiraSyncResult>>? OnSync { get; set; }

    public List<JiraSyncOptions> Runs { get; } = [];

    public Task<IReadOnlyList<JiraProject>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<JiraProject>>(Projects);

    public Task<JiraSyncResult> SyncAsync(JiraSyncOptions options, IProgress<JiraSyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Runs.Add(options);
        return OnSync?.Invoke(options, progress, cancellationToken)
            ?? Task.FromResult(new JiraSyncResult(JiraSyncStatus.Completed, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, null, []));
    }

    public JiraDataSummary GetSummary() => Summary;
}

internal static class TestViewModels
{
    public static App.ViewModels.JiraFetchViewModel Fetch(IJiraSession session, IJiraSyncService? sync = null) =>
        new(sync ?? new FakeSyncService(), session, NullLogger<App.ViewModels.JiraFetchViewModel>.Instance);

    public static App.ViewModels.JiraViewModel Jira(IJiraSession session, IDialogService dialogs) =>
        new(session, dialogs, Fetch(session));
}

internal static class TestSession
{
    public static JiraSession Create(
        IJiraConnectionService? connectionService = null,
        ISettingsStore? settingsStore = null,
        ICredentialStore? credentialStore = null) =>
        new(connectionService ?? FakeJiraConnectionService.Returning(JiraConnectionResult.Success(new JiraUser("Test User", null))),
            settingsStore ?? new InMemorySettingsStore(),
            credentialStore ?? new InMemoryCredentialStore(),
            NullLogger<JiraSession>.Instance);
}

using System.IO;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICollaborationPlatform.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AICollabTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private JsonSettingsStore CreateStore() => new(SettingsPath, NullLogger<JsonSettingsStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_loads_empty_settings()
    {
        Assert.Null(CreateStore().Load().Jira);
    }

    [Fact]
    public void Saves_and_loads_jira_connection()
    {
        var saved = new SavedJiraConnection("https://jira.acme.com/jira", JiraAuthType.Server, null);

        CreateStore().Save(new AppSettings { Jira = saved });

        Assert.Equal(saved, CreateStore().Load().Jira);
    }

    [Fact]
    public void File_is_readable_json_without_secrets()
    {
        CreateStore().Save(new AppSettings { Jira = new SavedJiraConnection("https://acme.atlassian.net", JiraAuthType.Cloud, "alex@acme.com") });

        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"authType\": \"Cloud\"", json);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_loads_empty_settings_instead_of_crashing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ this is not json");

        Assert.Null(CreateStore().Load().Jira);
    }
}

/// <summary>
/// Runs against the real Windows Credential Manager, using a unique entry that is always removed.
/// </summary>
public sealed class WindowsCredentialStoreTests : IDisposable
{
    private readonly WindowsCredentialStore _store = new();
    private readonly string _target = $"AICollaborationPlatform/Test/{Guid.NewGuid():N}";

    public void Dispose() => _store.Delete(_target);

    [Fact]
    public void Write_read_delete_round_trip()
    {
        _store.Write(_target, "alex@acme.com", "ATATT3xFfGF0-example-token");

        Assert.Equal("ATATT3xFfGF0-example-token", _store.Read(_target));
        Assert.True(_store.Delete(_target));
        Assert.Null(_store.Read(_target));
    }

    [Fact]
    public void Write_replaces_existing_secret()
    {
        _store.Write(_target, "user", "first");
        _store.Write(_target, "user", "second");

        Assert.Equal("second", _store.Read(_target));
    }

    [Fact]
    public void Missing_entry_reads_null_and_deletes_false()
    {
        Assert.Null(_store.Read(_target));
        Assert.False(_store.Delete(_target));
    }

    [Fact]
    public void Keeps_non_ascii_characters()
    {
        _store.Write(_target, "user", "tökén-✓-密码");

        Assert.Equal("tökén-✓-密码", _store.Read(_target));
    }

    [Fact]
    public void Rejects_secrets_over_the_windows_limit()
    {
        Assert.Throws<ArgumentException>(() => _store.Write(_target, "user", new string('x', 1281)));
        Assert.Null(_store.Read(_target));
    }
}

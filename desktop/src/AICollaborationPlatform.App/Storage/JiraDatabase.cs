using System.Globalization;
using System.IO;
using System.Text.Json;
using AICollaborationPlatform.App.Models;
using Microsoft.Data.Sqlite;

namespace AICollaborationPlatform.App.Storage;

/// <summary>Totals shown on the Jira page.</summary>
public sealed record JiraDataSummary(
    long Projects, long Issues, long Comments, long Users, long Boards, long Sprints,
    DateTimeOffset? LastFetchedAt, string? Site);

/// <summary>
/// The local SQLite copy of Jira data. Open one with <see cref="JiraDatabaseFactory"/>, use it, dispose it.
/// Dates are stored as UTC ISO 8601 text, so they sort and compare correctly as strings.
/// </summary>
public sealed class JiraDatabase : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private SqliteTransaction? _transaction;

    private JiraDatabase(SqliteConnection connection) => _connection = connection;

    public static JiraDatabase Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();

        var db = new JiraDatabase(connection);
        db.Execute("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;");
        db.EnsureSchema();
        return db;
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>Runs <paramref name="work"/> in a transaction: all of it is saved, or none of it.</summary>
    public void InTransaction(Action work)
    {
        _transaction = _connection.BeginTransaction();
        try
        {
            work();
            _transaction.Commit();
        }
        catch
        {
            _transaction.Rollback();
            throw;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    // ---- Site ----

    public string? GetSite() => GetMeta("site");

    public void SetSite(string site) => SetMeta("site", site);

    /// <summary>Removes all Jira data, e.g. before fetching from a different Jira site.</summary>
    public void ClearAllData() => Execute("""
        DELETE FROM changelog; DELETE FROM attachments; DELETE FROM comments; DELETE FROM issues;
        DELETE FROM sprints; DELETE FROM boards; DELETE FROM projects; DELETE FROM users; DELETE FROM fields;
        """);

    // ---- Writes ----

    public void UpsertField(JiraField field) => Execute("""
        INSERT INTO fields (id, name, custom, schema_type) VALUES ($id, $name, $custom, $type)
        ON CONFLICT(id) DO UPDATE SET name = excluded.name, custom = excluded.custom, schema_type = excluded.schema_type
        """, ("$id", field.Id), ("$name", field.Name), ("$custom", field.Custom), ("$type", field.SchemaType));

    public void UpsertProject(JiraProject project, DateTimeOffset fetchedAt)
    {
        // A project key can be reused after a project is deleted; the newest one wins.
        Execute("DELETE FROM projects WHERE key = $key AND id <> $id", ("$key", project.Key), ("$id", project.Id));
        Execute("""
            INSERT INTO projects (id, key, name, project_type, lead_id, lead_name, fetched_at)
            VALUES ($id, $key, $name, $type, $leadId, $leadName, $fetched)
            ON CONFLICT(id) DO UPDATE SET key = excluded.key, name = excluded.name, project_type = excluded.project_type,
                lead_id = excluded.lead_id, lead_name = excluded.lead_name, fetched_at = excluded.fetched_at
            """,
            ("$id", project.Id), ("$key", project.Key), ("$name", project.Name), ("$type", project.ProjectTypeKey),
            ("$leadId", project.Lead?.Id), ("$leadName", project.Lead?.DisplayName), ("$fetched", fetchedAt));
    }

    public void UpsertUser(JiraPerson person) => Execute("""
        INSERT INTO users (id, display_name, email, active) VALUES ($id, $name, $email, $active)
        ON CONFLICT(id) DO UPDATE SET display_name = excluded.display_name,
            email = COALESCE(excluded.email, users.email), active = excluded.active
        """, ("$id", person.Id), ("$name", person.DisplayName), ("$email", person.EmailAddress), ("$active", person.Active));

    /// <summary>
    /// Saves an issue and replaces its comments, attachments and history. Also records every person it mentions.
    /// <paramref name="comments"/> must be the issue's complete comment list.
    /// </summary>
    public void SaveIssue(JiraIssue issue, IReadOnlyList<JiraComment> comments, DateTimeOffset fetchedAt)
    {
        Execute("""
            INSERT INTO issues (id, key, project_key, summary, issue_type, status, status_category, priority,
                assignee_id, assignee_name, reporter_id, reporter_name, created, updated, resolution_date,
                description, labels, parent_key, fields_json, fetched_at)
            VALUES ($id, $key, $project, $summary, $type, $status, $statusCategory, $priority,
                $assigneeId, $assigneeName, $reporterId, $reporterName, $created, $updated, $resolved,
                $description, $labels, $parent, $fields, $fetched)
            ON CONFLICT(id) DO UPDATE SET key = excluded.key, project_key = excluded.project_key, summary = excluded.summary,
                issue_type = excluded.issue_type, status = excluded.status, status_category = excluded.status_category,
                priority = excluded.priority, assignee_id = excluded.assignee_id, assignee_name = excluded.assignee_name,
                reporter_id = excluded.reporter_id, reporter_name = excluded.reporter_name, created = excluded.created,
                updated = excluded.updated, resolution_date = excluded.resolution_date, description = excluded.description,
                labels = excluded.labels, parent_key = excluded.parent_key, fields_json = excluded.fields_json,
                fetched_at = excluded.fetched_at
            """,
            ("$id", issue.Id), ("$key", issue.Key), ("$project", issue.ProjectKey), ("$summary", issue.Summary),
            ("$type", issue.IssueType), ("$status", issue.Status), ("$statusCategory", issue.StatusCategory),
            ("$priority", issue.Priority), ("$assigneeId", issue.Assignee?.Id), ("$assigneeName", issue.Assignee?.DisplayName),
            ("$reporterId", issue.Reporter?.Id), ("$reporterName", issue.Reporter?.DisplayName),
            ("$created", issue.Created), ("$updated", issue.Updated), ("$resolved", issue.ResolutionDate),
            ("$description", issue.Description), ("$labels", JsonSerializer.Serialize(issue.Labels)),
            ("$parent", issue.ParentKey),
            ("$fields", issue.Fields.ValueKind == JsonValueKind.Undefined ? null : issue.Fields.GetRawText()),
            ("$fetched", fetchedAt));

        Execute("DELETE FROM comments WHERE issue_id = $id; DELETE FROM attachments WHERE issue_id = $id; DELETE FROM changelog WHERE issue_id = $id;",
            ("$id", issue.Id));

        foreach (var c in comments)
        {
            Execute("""
                INSERT OR REPLACE INTO comments (id, issue_id, author_id, author_name, body, created, updated)
                VALUES ($id, $issue, $authorId, $authorName, $body, $created, $updated)
                """,
                ("$id", c.Id), ("$issue", issue.Id), ("$authorId", c.Author?.Id), ("$authorName", c.Author?.DisplayName),
                ("$body", c.Body), ("$created", c.Created), ("$updated", c.Updated));
        }

        foreach (var a in issue.Attachments)
        {
            Execute("""
                INSERT OR REPLACE INTO attachments (id, issue_id, filename, size, mime_type, created, author_name, content_url)
                VALUES ($id, $issue, $name, $size, $mime, $created, $author, $url)
                """,
                ("$id", a.Id), ("$issue", issue.Id), ("$name", a.Filename), ("$size", a.Size), ("$mime", a.MimeType),
                ("$created", a.Created), ("$author", a.Author?.DisplayName), ("$url", a.ContentUrl));
        }

        foreach (var change in issue.Changes)
        {
            Execute("""
                INSERT INTO changelog (issue_id, history_id, author_name, created, field, from_value, to_value)
                VALUES ($issue, $history, $author, $created, $field, $from, $to)
                """,
                ("$issue", issue.Id), ("$history", change.HistoryId), ("$author", change.Author?.DisplayName),
                ("$created", change.Created), ("$field", change.Field), ("$from", change.From), ("$to", change.To));
        }

        var people = new[] { issue.Assignee, issue.Reporter }
            .Concat(comments.Select(c => c.Author))
            .Concat(issue.Attachments.Select(a => a.Author))
            .Concat(issue.Changes.Select(c => c.Author))
            .OfType<JiraPerson>()
            .DistinctBy(p => p.Id);
        foreach (var person in people)
        {
            UpsertUser(person);
        }
    }

    /// <summary>After a full fetch of a project, removes issues Jira no longer has (deleted or moved away).</summary>
    public int DeleteIssuesNotIn(string projectKey, IReadOnlyCollection<string> keepIssueIds)
    {
        Execute("CREATE TEMP TABLE IF NOT EXISTS keep_ids (id TEXT PRIMARY KEY); DELETE FROM keep_ids;");
        foreach (var id in keepIssueIds)
        {
            Execute("INSERT OR IGNORE INTO keep_ids (id) VALUES ($id)", ("$id", id));
        }

        var deleted = Execute("DELETE FROM issues WHERE project_key = $project AND id NOT IN (SELECT id FROM keep_ids)", ("$project", projectKey));
        Execute("DELETE FROM keep_ids");
        return deleted;
    }

    public void UpsertBoard(JiraBoard board) => Execute("""
        INSERT INTO boards (id, name, type, project_key) VALUES ($id, $name, $type, $project)
        ON CONFLICT(id) DO UPDATE SET name = excluded.name, type = excluded.type, project_key = excluded.project_key
        """, ("$id", board.Id), ("$name", board.Name), ("$type", board.Type), ("$project", board.ProjectKey));

    public void UpsertSprint(JiraSprint sprint) => Execute("""
        INSERT INTO sprints (id, board_id, name, state, start_date, end_date, complete_date, goal)
        VALUES ($id, $board, $name, $state, $start, $end, $complete, $goal)
        ON CONFLICT(id) DO UPDATE SET board_id = excluded.board_id, name = excluded.name, state = excluded.state,
            start_date = excluded.start_date, end_date = excluded.end_date, complete_date = excluded.complete_date, goal = excluded.goal
        """,
        ("$id", sprint.Id), ("$board", sprint.BoardId), ("$name", sprint.Name), ("$state", sprint.State),
        ("$start", sprint.StartDate), ("$end", sprint.EndDate), ("$complete", sprint.CompleteDate), ("$goal", sprint.Goal));

    // ---- Incremental sync bookkeeping ----

    /// <summary>When the project was last fully fetched, and with which JQL filter (null = none).</summary>
    public (DateTimeOffset? SyncedAt, string? Filter) GetProjectSyncState(string projectKey)
    {
        using var command = CreateCommand("SELECT last_synced_at, last_sync_filter FROM projects WHERE key = $key", ("$key", projectKey));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.IsDBNull(0) ? null : ParseDate(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1))
            : (null, null);
    }

    public void MarkProjectSynced(string projectKey, DateTimeOffset syncedAt, string? filter) =>
        Execute("UPDATE projects SET last_synced_at = $at, last_sync_filter = $filter WHERE key = $key",
            ("$at", syncedAt), ("$filter", filter), ("$key", projectKey));

    public long StartSyncRun(DateTimeOffset startedAt, string? jqlFilter)
    {
        Execute("INSERT INTO sync_runs (started_at, status, jql_filter) VALUES ($at, 'running', $jql)", ("$at", startedAt), ("$jql", jqlFilter));
        return Scalar<long>("SELECT last_insert_rowid()");
    }

    public void FinishSyncRun(long runId, string status, DateTimeOffset finishedAt, int projects, int issues, int comments, string? error) =>
        Execute("""
            UPDATE sync_runs SET status = $status, finished_at = $at, projects = $projects, issues = $issues,
                comments = $comments, error = $error WHERE id = $id
            """,
            ("$status", status), ("$at", finishedAt), ("$projects", projects), ("$issues", issues),
            ("$comments", comments), ("$error", error), ("$id", runId));

    // ---- Reads ----

    public JiraDataSummary GetSummary()
    {
        var lastFetched = Scalar<string?>("SELECT MAX(finished_at) FROM sync_runs WHERE status = 'completed'");
        return new JiraDataSummary(
            Scalar<long>("SELECT COUNT(*) FROM projects WHERE last_synced_at IS NOT NULL"),
            Scalar<long>("SELECT COUNT(*) FROM issues"),
            Scalar<long>("SELECT COUNT(*) FROM comments"),
            Scalar<long>("SELECT COUNT(*) FROM users"),
            Scalar<long>("SELECT COUNT(*) FROM boards"),
            Scalar<long>("SELECT COUNT(*) FROM sprints"),
            lastFetched is null ? null : ParseDate(lastFetched),
            GetSite());
    }

    private static DateTimeOffset ParseDate(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    public long Count(string table) => Scalar<long>($"SELECT COUNT(*) FROM {table}");

    // ---- Plumbing ----

    private void EnsureSchema()
    {
        var version = Scalar<long>("PRAGMA user_version");
        if (version >= SchemaVersion)
        {
            return;
        }

        Execute("""
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS fields (id TEXT PRIMARY KEY, name TEXT NOT NULL, custom INTEGER NOT NULL, schema_type TEXT);
            CREATE TABLE IF NOT EXISTS projects (
                id TEXT PRIMARY KEY, key TEXT NOT NULL, name TEXT NOT NULL, project_type TEXT,
                lead_id TEXT, lead_name TEXT, fetched_at TEXT NOT NULL, last_synced_at TEXT, last_sync_filter TEXT);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_projects_key ON projects(key);
            CREATE TABLE IF NOT EXISTS users (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, email TEXT, active INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS issues (
                id TEXT PRIMARY KEY, key TEXT NOT NULL, project_key TEXT, summary TEXT NOT NULL,
                issue_type TEXT, status TEXT, status_category TEXT, priority TEXT,
                assignee_id TEXT, assignee_name TEXT, reporter_id TEXT, reporter_name TEXT,
                created TEXT, updated TEXT, resolution_date TEXT, description TEXT,
                labels TEXT NOT NULL, parent_key TEXT, fields_json TEXT, fetched_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_issues_key ON issues(key);
            CREATE INDEX IF NOT EXISTS ix_issues_project ON issues(project_key);
            CREATE INDEX IF NOT EXISTS ix_issues_updated ON issues(updated);
            CREATE TABLE IF NOT EXISTS comments (
                id TEXT PRIMARY KEY, issue_id TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
                author_id TEXT, author_name TEXT, body TEXT NOT NULL, created TEXT, updated TEXT);
            CREATE INDEX IF NOT EXISTS ix_comments_issue ON comments(issue_id);
            CREATE TABLE IF NOT EXISTS attachments (
                id TEXT PRIMARY KEY, issue_id TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
                filename TEXT NOT NULL, size INTEGER NOT NULL, mime_type TEXT, created TEXT, author_name TEXT, content_url TEXT);
            CREATE INDEX IF NOT EXISTS ix_attachments_issue ON attachments(issue_id);
            CREATE TABLE IF NOT EXISTS changelog (
                id INTEGER PRIMARY KEY AUTOINCREMENT, issue_id TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
                history_id TEXT, author_name TEXT, created TEXT, field TEXT NOT NULL, from_value TEXT, to_value TEXT);
            CREATE INDEX IF NOT EXISTS ix_changelog_issue ON changelog(issue_id);
            CREATE TABLE IF NOT EXISTS boards (id INTEGER PRIMARY KEY, name TEXT NOT NULL, type TEXT, project_key TEXT);
            CREATE TABLE IF NOT EXISTS sprints (
                id INTEGER PRIMARY KEY, board_id INTEGER, name TEXT NOT NULL, state TEXT,
                start_date TEXT, end_date TEXT, complete_date TEXT, goal TEXT);
            CREATE TABLE IF NOT EXISTS sync_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT, started_at TEXT NOT NULL, finished_at TEXT, status TEXT NOT NULL,
                projects INTEGER, issues INTEGER, comments INTEGER, jql_filter TEXT, error TEXT);
            """);
        Execute($"PRAGMA user_version = {SchemaVersion}");
    }

    private string? GetMeta(string key) => Scalar<string?>("SELECT value FROM meta WHERE key = $key", ("$key", key));

    private void SetMeta(string key, string value) =>
        Execute("INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private SqliteCommand CreateCommand(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction; // Microsoft.Data.Sqlite requires this while a transaction is open
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value switch
            {
                null => DBNull.Value,
                DateTimeOffset date => date.ToUniversalTime().ToString("O"),
                bool b => b ? 1 : 0,
                _ => value,
            });
        }

        return command;
    }
}

public sealed class JiraDatabaseFactory(string path)
{
    public string Path { get; } = path;

    public JiraDatabase Open() => JiraDatabase.Open(Path);
}

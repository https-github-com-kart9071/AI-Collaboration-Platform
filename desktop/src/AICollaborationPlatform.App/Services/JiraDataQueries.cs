using System.IO;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Storage;

namespace AICollaborationPlatform.App.Services;

/// <summary>Everything the data browser shows at once.</summary>
public sealed record JiraDataSnapshot(string? Site, IReadOnlyList<ProjectRow> Projects, IReadOnlyList<IssueRow> Issues)
{
    public static readonly JiraDataSnapshot Empty = new(null, [], []);
}

/// <summary>Reads the local Jira copy for browsing. Works offline; never calls Jira.</summary>
public interface IJiraDataQueries
{
    JiraDataSnapshot LoadSnapshot();

    IssueDetail? LoadIssueDetail(string issueId);
}

public sealed class JiraDataQueries(JiraDatabaseFactory databaseFactory) : IJiraDataQueries
{
    public JiraDataSnapshot LoadSnapshot()
    {
        // No fetch yet: don't create an empty database just to read nothing from it.
        if (!File.Exists(databaseFactory.Path))
        {
            return JiraDataSnapshot.Empty;
        }

        using var db = databaseFactory.Open();
        return new JiraDataSnapshot(db.GetSite(), db.GetProjects(), db.GetIssueRows());
    }

    public IssueDetail? LoadIssueDetail(string issueId)
    {
        if (!File.Exists(databaseFactory.Path))
        {
            return null;
        }

        using var db = databaseFactory.Open();
        return db.GetIssueDetail(issueId);
    }
}

using AICollaborationPlatform.App.Features.Jira;

namespace AICollaborationPlatform.App.Core;

/// <summary>
/// Everything saved in settings.json. Never put secrets here.
/// Each feature owns the type of its own section (e.g. <see cref="SavedJiraConnection"/>); this record only gathers them.
/// </summary>
public sealed record AppSettings
{
    public SavedJiraConnection? Jira { get; init; }
}

namespace AICollaborationPlatform.App.Features.Code;

/// <summary>Where the repositories live. Each kind gets its own API client later; Other is plain Git over HTTPS.</summary>
public enum CodeHostKind
{
    GitHub,
    GitLab,
    Bitbucket,
    AzureDevOps,
    Other,
}

/// <summary>
/// One choice in the Connect dialog. <see cref="Color"/> tints its letter tile (a colour string, e.g. "#FC6D26").
/// <see cref="CloudUrl"/> is the provider's own hosted service; null for Other, which always needs an address.
/// </summary>
public sealed record CodeHostOption(
    CodeHostKind Kind,
    string Name,
    string Description,
    string Letter,
    string Color,
    string? CloudName,
    string? CloudUrl,
    string SelfHostedName,
    string ServerExample)
{
    public bool HasCloud => CloudUrl is not null;

    public static IReadOnlyList<CodeHostOption> All { get; } =
    [
        new(CodeHostKind.GitHub, "GitHub", "github.com or GitHub Enterprise Server", "G", "#6E7781",
            "GitHub.com", "https://github.com", "GitHub Enterprise Server", "https://github.mycompany.com"),
        new(CodeHostKind.GitLab, "GitLab", "gitlab.com or your company's own GitLab", "G", "#E2432A",
            "GitLab.com", "https://gitlab.com", "Self-managed GitLab", "https://gitlab.mycompany.com"),
        new(CodeHostKind.Bitbucket, "Bitbucket", "bitbucket.org or Bitbucket Data Center", "B", "#2684FF",
            "Bitbucket Cloud", "https://bitbucket.org", "Bitbucket Data Center", "https://bitbucket.mycompany.com"),
        new(CodeHostKind.AzureDevOps, "Azure DevOps", "dev.azure.com or Azure DevOps Server", "A", "#0078D4",
            "Azure DevOps Services", "https://dev.azure.com", "Azure DevOps Server", "https://devops.mycompany.com/tfs/DefaultCollection"),
        new(CodeHostKind.Other, "Other", "Any Git repository, by its URL (https://…/repo.git)", "•", "#F05033",
            null, null, "Git server", "https://git.mycompany.com/team/project.git"),
    ];
}

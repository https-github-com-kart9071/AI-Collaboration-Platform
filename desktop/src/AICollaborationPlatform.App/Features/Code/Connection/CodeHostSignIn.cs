namespace AICollaborationPlatform.App.Features.Code;

/// <summary>
/// What the sign-in step asks for on one host: the token's name, where to create it, the read-only
/// permissions it needs, and whether a username goes with it.
/// </summary>
/// <param name="UsernameLabel">Null when the token alone is enough.</param>
/// <param name="CreateTokenUrl">Null when there's no single page to send people to (Other).</param>
public sealed record CodeHostSignIn(
    string TokenLabel,
    string? UsernameLabel,
    string? CreateTokenUrl,
    string CreateTokenText,
    string Permissions)
{
    public static CodeHostSignIn For(CodeHostOption host, Uri server)
    {
        var root = server.AbsoluteUri.TrimEnd('/');
        var cloud = host.CloudUrl is not null && server.Host.Equals(new Uri(host.CloudUrl).Host, StringComparison.OrdinalIgnoreCase);
        return host.Kind switch
        {
            CodeHostKind.GitHub => new(
                "Personal access token", null,
                cloud ? "https://github.com/settings/personal-access-tokens/new" : $"{root}/settings/tokens/new",
                "Create a token on GitHub",
                cloud
                    ? "Use a fine-grained token with read-only access to Contents and Metadata for the repositories you want to see."
                    : "Use a token with the repo scope (read access to your repositories)."),
            CodeHostKind.GitLab => new(
                "Personal access token", null,
                $"{root}/-/user_settings/personal_access_tokens",
                "Create a token on GitLab",
                "Select the read_api and read_repository scopes. Nothing else is needed."),
            CodeHostKind.Bitbucket when cloud => new(
                "API token", "Atlassian account email",
                "https://id.atlassian.com/manage-profile/security/api-tokens",
                "Create an API token",
                "Create an API token with scopes for Bitbucket: read:repository:bitbucket and read:user:bitbucket."),
            CodeHostKind.Bitbucket => new(
                "HTTP access token", null,
                $"{root}/plugins/servlet/access-tokens/manage",
                "Create a token in Bitbucket",
                "Give the token Repository read and Project read permission."),
            CodeHostKind.AzureDevOps => new(
                "Personal access token", null,
                cloud
                    ? "https://learn.microsoft.com/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate"
                    : $"{root}/_usersSettings/tokens",
                cloud ? "How to create a token" : "Create a token in Azure DevOps",
                "Give the token the Code (Read) scope only."),
            _ => new(
                "Password or access token", "Username",
                null,
                "",
                "Use an access token or app password from your Git server with read access to this repository. Avoid your main password where the server offers tokens."),
        };
    }
}

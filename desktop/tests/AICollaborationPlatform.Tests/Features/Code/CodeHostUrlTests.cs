using AICollaborationPlatform.App.Features.Code;

namespace AICollaborationPlatform.Tests;

public class CodeHostUrlTests
{
    private static CodeHostOption Host(CodeHostKind kind) => CodeHostOption.All.Single(h => h.Kind == kind);

    [Theory]
    [InlineData(CodeHostKind.GitLab, "https://gitlab.mycompany.com", "https://gitlab.mycompany.com/")]
    [InlineData(CodeHostKind.GitLab, "  gitlab.mycompany.com/  ", "https://gitlab.mycompany.com/")] // https:// added, spaces and slash dropped
    [InlineData(CodeHostKind.GitLab, "https://gitlab.acme.com/team/app/-/issues?x=1#top", "https://gitlab.acme.com/")] // pasted page -> server
    [InlineData(CodeHostKind.GitHub, "https://github.acme.com:8443/org/repo", "https://github.acme.com:8443/")] // port kept
    [InlineData(CodeHostKind.Bitbucket, "https://acme.com/bitbucket/", "https://acme.com/bitbucket")] // context path kept
    [InlineData(CodeHostKind.AzureDevOps, "https://devops.acme.com/tfs/DefaultCollection", "https://devops.acme.com/tfs/DefaultCollection")]
    [InlineData(CodeHostKind.Other, "https://git.acme.com/team/project.git", "https://git.acme.com/team/project.git")]
    public void Cleans_up_valid_addresses(CodeHostKind kind, string input, string expected)
    {
        Assert.True(CodeHostUrl.TryNormalize(Host(kind), input, out var address, out var error), error);
        Assert.Equal(expected, address!.AbsoluteUri);
    }

    [Theory]
    [InlineData(CodeHostKind.GitLab, "", "Enter your Self-managed GitLab address.")]
    [InlineData(CodeHostKind.Other, "  ", "Enter the repository URL.")]
    [InlineData(CodeHostKind.GitLab, "http://gitlab.acme.com", "The address must start with https://")]
    [InlineData(CodeHostKind.GitLab, "not a url", "This isn't a valid address")]
    [InlineData(CodeHostKind.Other, "git@github.com:acme/app.git", "SSH addresses aren't supported yet")]
    [InlineData(CodeHostKind.Other, "https://git.acme.com", "Enter the full repository URL")]
    [InlineData(CodeHostKind.GitHub, "https://github.com", "That's GitHub.com. Choose GitHub.com above")]
    [InlineData(CodeHostKind.GitHub, "www.github.com/acme", "That's GitHub.com")]
    [InlineData(CodeHostKind.GitLab, "gitlab.com", "That's GitLab.com")]
    [InlineData(CodeHostKind.AzureDevOps, "https://acme.visualstudio.com", "That's Azure DevOps Services")]
    public void Explains_what_is_wrong(CodeHostKind kind, string input, string expectedStart)
    {
        Assert.False(CodeHostUrl.TryNormalize(Host(kind), input, out var address, out var error));
        Assert.Null(address);
        Assert.StartsWith(expectedStart, error);
    }
}

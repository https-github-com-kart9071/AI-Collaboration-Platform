using AICollaborationPlatform.App.Services;

namespace AICollaborationPlatform.Tests;

public class JiraUrlTests
{
    [Theory]
    [InlineData("https://acme.atlassian.net", "https://acme.atlassian.net/")]
    [InlineData("  https://acme.atlassian.net/  ", "https://acme.atlassian.net/")]
    [InlineData("https://acme.atlassian.net/browse/ABC-1?focus=1#c", "https://acme.atlassian.net/")]
    [InlineData("https://acme.atlassian.net/jira/software/projects/ABC/boards/1", "https://acme.atlassian.net/")]
    [InlineData("https://jira.acme.com", "https://jira.acme.com/")]
    [InlineData("https://jira.acme.com/", "https://jira.acme.com/")]
    [InlineData("https://acme.com/jira/", "https://acme.com/jira")]
    [InlineData("https://jira.acme.com:8443/jira?x=1", "https://jira.acme.com:8443/jira")]
    [InlineData("https://jira.acme.com/browse/ABC-1", "https://jira.acme.com/")]
    [InlineData("https://acme.com/jira/browse/ABC-1?focusedCommentId=5", "https://acme.com/jira")]
    [InlineData("https://acme.com/jira/secure/RapidBoard.jspa?rapidView=1", "https://acme.com/jira")]
    [InlineData("https://acme.com/tools/jira/projects/ABC/issues", "https://acme.com/tools/jira")]
    public void Normalizes_valid_urls(string input, string expected)
    {
        Assert.True(JiraUrl.TryNormalize(input, out var url, out var error));
        Assert.Equal(expected, url!.AbsoluteUri);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData(null, "Enter your Jira URL.")]
    [InlineData("   ", "Enter your Jira URL.")]
    [InlineData("acme.atlassian.net", "This isn't a valid URL")]
    [InlineData("not a url", "This isn't a valid URL")]
    [InlineData("http://jira.acme.com", "must start with https://")]
    [InlineData("ftp://jira.acme.com", "must start with https://")]
    public void Rejects_invalid_urls(string? input, string expectedError)
    {
        Assert.False(JiraUrl.TryNormalize(input, out var url, out var error));
        Assert.Null(url);
        Assert.Contains(expectedError, error);
    }
}

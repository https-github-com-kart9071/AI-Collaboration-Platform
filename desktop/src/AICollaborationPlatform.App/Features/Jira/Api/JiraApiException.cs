using System.Net;

namespace AICollaborationPlatform.App.Services;

/// <summary>Jira answered with an error, even after retries.</summary>
public sealed class JiraApiException(HttpStatusCode statusCode, string message, IReadOnlyList<string> jiraErrors)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>The errorMessages / errors Jira returned, if any.</summary>
    public IReadOnlyList<string> JiraErrors { get; } = jiraErrors;
}

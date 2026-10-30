namespace AICollaborationPlatform.App.Models;

public enum JiraConnectionError
{
    None,
    HostUnreachable,
    Timeout,
    SslError,
    InvalidCredentials,
    Forbidden,
    NotJira,
    UnexpectedResponse,
}

public sealed record JiraConnectionResult(JiraUser? User, JiraConnectionError Error, string Message, string? Details = null)
{
    public bool IsSuccess => Error == JiraConnectionError.None;

    public static JiraConnectionResult Success(JiraUser user) =>
        new(user, JiraConnectionError.None, $"Connected as {user.DisplayName}");

    public static JiraConnectionResult Failure(JiraConnectionError error, string message, string? details = null) =>
        new(null, error, message, details);
}

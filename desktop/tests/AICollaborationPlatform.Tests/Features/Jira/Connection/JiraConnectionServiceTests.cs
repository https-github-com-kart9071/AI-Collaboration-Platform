using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICollaborationPlatform.Tests;

public class JiraConnectionServiceTests
{
    private static readonly JiraConnectionSettings Cloud =
        new(new Uri("https://acme.atlassian.net"), JiraAuthType.Cloud, "alex@acme.com", "cloud-token");

    private static readonly JiraConnectionSettings Server =
        new(new Uri("https://acme.com/jira"), JiraAuthType.Server, null, "server-pat");

    private const string MyselfJson = """{"accountId":"1","displayName":"Alex Kim","emailAddress":"alex@acme.com"}""";

    private static JiraConnectionService CreateService(StubHttpHandler handler) =>
        new(new HttpClient(handler), NullLogger<JiraConnectionService>.Instance);

    [Fact]
    public async Task Cloud_uses_v3_and_basic_auth_with_email_and_token()
    {
        var handler = StubHttpHandler.Returning(HttpStatusCode.OK, MyselfJson);

        await CreateService(handler).TestConnectionAsync(Cloud);

        var request = handler.LastRequest!;
        Assert.Equal("https://acme.atlassian.net/rest/api/3/myself", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("alex@acme.com:cloud-token",
            Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
    }

    [Fact]
    public async Task Server_uses_v2_under_context_path_and_bearer_token()
    {
        var handler = StubHttpHandler.Returning(HttpStatusCode.OK, MyselfJson);

        await CreateService(handler).TestConnectionAsync(Server);

        var request = handler.LastRequest!;
        Assert.Equal("https://acme.com/jira/rest/api/2/myself", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("server-pat", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Success_returns_display_name()
    {
        var result = await CreateService(StubHttpHandler.Returning(HttpStatusCode.OK, MyselfJson)).TestConnectionAsync(Cloud);

        Assert.True(result.IsSuccess);
        Assert.Equal("Alex Kim", result.User!.DisplayName);
        Assert.Equal("Connected as Alex Kim", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, JiraConnectionError.InvalidCredentials)]
    [InlineData(HttpStatusCode.Forbidden, JiraConnectionError.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, JiraConnectionError.NotJira)]
    [InlineData(HttpStatusCode.InternalServerError, JiraConnectionError.UnexpectedResponse)]
    [InlineData(HttpStatusCode.ServiceUnavailable, JiraConnectionError.UnexpectedResponse)]
    public async Task Maps_http_status_to_error(HttpStatusCode status, JiraConnectionError expected)
    {
        var result = await CreateService(StubHttpHandler.Returning(status)).TestConnectionAsync(Cloud);

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Theory]
    [InlineData("<html><body>Log in</body></html>", "text/html")]
    [InlineData("""{"message":"hello"}""", "application/json")]
    [InlineData("[]", "application/json")]
    public async Task Ok_response_that_is_not_jira_user_is_not_jira(string body, string mediaType)
    {
        var result = await CreateService(StubHttpHandler.Returning(HttpStatusCode.OK, body, mediaType)).TestConnectionAsync(Cloud);

        Assert.Equal(JiraConnectionError.NotJira, result.Error);
    }

    [Fact]
    public async Task Unknown_host_is_unreachable()
    {
        var handler = StubHttpHandler.Throwing(new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."));

        var result = await CreateService(handler).TestConnectionAsync(Cloud);

        Assert.Equal(JiraConnectionError.HostUnreachable, result.Error);
        Assert.Contains("acme.atlassian.net", result.Message);
        Assert.Equal("No such host is known.", result.Details);
    }

    [Fact]
    public async Task Refused_connection_is_unreachable()
    {
        var handler = StubHttpHandler.Throwing(new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused."));

        var result = await CreateService(handler).TestConnectionAsync(Cloud);

        Assert.Equal(JiraConnectionError.HostUnreachable, result.Error);
    }

    [Fact]
    public async Task Certificate_problem_is_ssl_error()
    {
        var handler = StubHttpHandler.Throwing(new HttpRequestException(
            HttpRequestError.SecureConnectionError, "The SSL connection could not be established.",
            new AuthenticationException("The remote certificate is invalid.")));

        var result = await CreateService(handler).TestConnectionAsync(Cloud);

        Assert.Equal(JiraConnectionError.SslError, result.Error);
    }

    [Fact]
    public async Task HttpClient_timeout_is_timeout_error()
    {
        var handler = StubHttpHandler.Throwing(new TaskCanceledException("timed out", new TimeoutException()));

        var result = await CreateService(handler).TestConnectionAsync(Cloud);

        Assert.Equal(JiraConnectionError.Timeout, result.Error);
    }

    [Fact]
    public async Task Caller_cancellation_throws()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHttpHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var task = CreateService(handler).TestConnectionAsync(Cloud, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void Settings_ToString_never_contains_the_token()
    {
        Assert.DoesNotContain("cloud-token", Cloud.ToString());
        Assert.DoesNotContain("server-pat", Server.ToString());
    }
}

using System.Net;
using System.Net.Http;
using AICollaborationPlatform.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICollaborationPlatform.Tests;

public class JiraRetryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Sut
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public Sut(params Func<HttpResponseMessage>[] responses)
        {
            foreach (var r in responses) _responses.Enqueue(r);
            var handler = new JiraRetryHandler(NullLogger<JiraRetryHandler>.Instance)
            {
                InnerHandler = new StubHttpHandler((_, _) => { Attempts++; return Task.FromResult(_responses.Dequeue()()); }),
                Delay = (d, ct) => { ct.ThrowIfCancellationRequested(); Delays.Add(d); return Task.CompletedTask; },
                Jitter = () => TimeSpan.Zero,
                Now = () => Now,
            };
            Client = new HttpClient(handler);
        }

        public HttpClient Client { get; }

        public int Attempts { get; private set; }

        public List<TimeSpan> Delays { get; } = [];
    }

    private static Func<HttpResponseMessage> Status(HttpStatusCode status) => () => new HttpResponseMessage(status);

    private static Func<HttpResponseMessage> Throw(HttpRequestError error) => () => throw new HttpRequestException(error, error.ToString());

    [Fact]
    public async Task Success_is_returned_without_retrying()
    {
        var sut = new Sut(Status(HttpStatusCode.OK));

        var response = await sut.Client.GetAsync("https://jira.test/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, sut.Attempts);
        Assert.Empty(sut.Delays);
    }

    [Fact]
    public async Task Server_errors_back_off_exponentially_then_succeed()
    {
        var sut = new Sut(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.InternalServerError), Status(HttpStatusCode.OK));

        var response = await sut.Client.GetAsync("https://jira.test/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, sut.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], sut.Delays);
    }

    [Fact]
    public async Task Gives_up_after_three_retries_and_returns_the_last_response()
    {
        var sut = new Sut(Status(HttpStatusCode.GatewayTimeout), Status(HttpStatusCode.GatewayTimeout), Status(HttpStatusCode.GatewayTimeout), Status(HttpStatusCode.GatewayTimeout));

        var response = await sut.Client.GetAsync("https://jira.test/x");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(1 + JiraRetryHandler.MaxRetries, sut.Attempts);
    }

    [Fact]
    public async Task Retry_after_seconds_is_honoured()
    {
        var sut = new Sut(
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(7)) } },
            Status(HttpStatusCode.OK));

        await sut.Client.GetAsync("https://jira.test/x");

        Assert.Equal([TimeSpan.FromSeconds(7)], sut.Delays);
    }

    [Fact]
    public async Task Retry_after_date_is_honoured()
    {
        var sut = new Sut(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Headers = { RetryAfter = new(Now.AddSeconds(12)) } },
            Status(HttpStatusCode.OK));

        await sut.Client.GetAsync("https://jira.test/x");

        Assert.Equal([TimeSpan.FromSeconds(12)], sut.Delays);
    }

    [Fact]
    public async Task Retry_after_in_the_past_retries_immediately()
    {
        var sut = new Sut(
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(Now.AddSeconds(-5)) } },
            Status(HttpStatusCode.OK));

        await sut.Client.GetAsync("https://jira.test/x");

        Assert.Equal([TimeSpan.Zero], sut.Delays);
    }

    [Fact]
    public async Task Very_long_retry_after_is_capped()
    {
        var sut = new Sut(
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromHours(1)) } },
            Status(HttpStatusCode.OK));

        await sut.Client.GetAsync("https://jira.test/x");

        Assert.Equal([JiraRetryHandler.MaxDelay], sut.Delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task Permanent_errors_are_not_retried(HttpStatusCode status)
    {
        var sut = new Sut(Status(status));

        var response = await sut.Client.GetAsync("https://jira.test/x");

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, sut.Attempts);
    }

    [Fact]
    public async Task Post_is_never_retried()
    {
        var sut = new Sut(Status(HttpStatusCode.ServiceUnavailable));

        var response = await sut.Client.PostAsync("https://jira.test/x", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, sut.Attempts);
    }

    [Fact]
    public async Task Dropped_connections_are_retried()
    {
        var sut = new Sut(Throw(HttpRequestError.ConnectionError), Throw(HttpRequestError.ResponseEnded), Status(HttpStatusCode.OK));

        var response = await sut.Client.GetAsync("https://jira.test/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], sut.Delays);
    }

    [Fact]
    public async Task Connection_errors_give_up_after_three_retries()
    {
        var sut = new Sut(Throw(HttpRequestError.ConnectionError), Throw(HttpRequestError.ConnectionError), Throw(HttpRequestError.ConnectionError), Throw(HttpRequestError.ConnectionError));

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.Client.GetAsync("https://jira.test/x"));
        Assert.Equal(4, sut.Attempts);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public async Task Unknown_host_and_certificate_errors_are_not_retried(HttpRequestError error)
    {
        var sut = new Sut(Throw(error));

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.Client.GetAsync("https://jira.test/x"));
        Assert.Equal(1, sut.Attempts);
    }

    [Fact]
    public async Task Cancellation_during_the_wait_stops_retrying()
    {
        using var cts = new CancellationTokenSource();
        var sut = new Sut(() => { cts.Cancel(); return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }, Status(HttpStatusCode.OK));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.Client.GetAsync("https://jira.test/x", cts.Token));
        Assert.Equal(1, sut.Attempts);
    }

    [Fact]
    public void Default_backoff_includes_jitter_within_a_quarter_second()
    {
        var handler = new JiraRetryHandler(NullLogger<JiraRetryHandler>.Instance);

        for (var i = 0; i < 50; i++)
        {
            var jitter = handler.Jitter();
            Assert.InRange(jitter, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
        }
    }
}

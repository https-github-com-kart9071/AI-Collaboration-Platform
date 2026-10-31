using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.App.Services;

/// <summary>
/// Retries Jira requests that failed for temporary reasons: rate limiting (429), server errors (5xx)
/// and dropped connections. Waits as long as Jira's Retry-After header asks, otherwise backs off
/// exponentially (1s, 2s, 4s) with a little random jitter. Only GET/HEAD are retried, since they're safe to repeat.
/// </summary>
public sealed class JiraRetryHandler(ILogger<JiraRetryHandler> logger) : DelegatingHandler
{
    public const int MaxRetries = 3;

    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>Never wait longer than this for one retry, whatever Retry-After says.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    // Replaceable so tests don't actually sleep.
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (delay, ct) => Task.Delay(delay, ct);

    public Func<TimeSpan> Jitter { get; init; } = () => TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));

    /// <summary>Overrides "now" when reading a Retry-After date. Tests set it.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var canRetry = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (canRetry && attempt < MaxRetries && IsTransient(ex))
            {
                var wait = Backoff(attempt);
                logger.LogWarning("Jira request {Path} failed ({Error}); retry {Attempt}/{Max} in {Delay:0.0}s",
                    request.RequestUri?.AbsolutePath, ex.HttpRequestError, attempt + 1, MaxRetries, wait.TotalSeconds);
                await Delay(wait, cancellationToken);
                continue;
            }

            if (!canRetry || attempt >= MaxRetries || !IsRetryable(response.StatusCode))
            {
                return response;
            }

            var delay = RetryAfter(response) ?? Backoff(attempt);
            logger.LogWarning("Jira returned {Status} for {Path}; retry {Attempt}/{Max} in {Delay:0.0}s",
                (int)response.StatusCode, request.RequestUri?.AbsolutePath, attempt + 1, MaxRetries, delay.TotalSeconds);
            response.Dispose();
            await Delay(delay, cancellationToken);
        }
    }

    public static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    // A refused or dropped connection may work next time; a bad host name or certificate won't.
    private static bool IsTransient(HttpRequestException ex) =>
        ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded;

    private TimeSpan Backoff(int attempt) => Cap(BaseDelay * Math.Pow(2, attempt) + Jitter());

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - Now() : null);
        return delay is { } d ? Cap(d < TimeSpan.Zero ? TimeSpan.Zero : d) : null;
    }

    private static TimeSpan Cap(TimeSpan delay) => delay > MaxDelay ? MaxDelay : delay;
}

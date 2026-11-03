using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;
using AICollaborationPlatform.App.Models;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.App.Services;

public sealed class JiraConnectionService(HttpClient http, ILogger<JiraConnectionService> logger) : IJiraConnectionService
{
    public async Task<JiraConnectionResult> TestConnectionAsync(JiraConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var request = JiraAuth.CreateRequest(settings, HttpMethod.Get, $"rest/api/{JiraAuth.ApiVersion(settings)}/myself");

        logger.LogInformation("Testing Jira connection: {Settings}", settings);

        var result = await SendAsync(request, settings, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("Jira connection OK for {Host} as {User}", settings.BaseUrl.Host, result.User!.DisplayName);
        }
        else
        {
            logger.LogWarning("Jira connection to {Host} failed: {Error} {Details}", settings.BaseUrl.Host, result.Error, result.Details);
        }

        return result;
    }

    private async Task<JiraConnectionResult> SendAsync(HttpRequestMessage request, JiraConnectionSettings settings, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // Not cancelled by the caller, so HttpClient.Timeout expired.
            return JiraConnectionResult.Failure(JiraConnectionError.Timeout,
                "Jira didn't respond in time. Check the URL and your network, then try again.", ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return MapRequestException(ex, settings.BaseUrl);
        }

        using (response)
        {
            return response.StatusCode switch
            {
                HttpStatusCode.OK => await ParseUserAsync(response, cancellationToken),
                HttpStatusCode.Unauthorized => JiraConnectionResult.Failure(JiraConnectionError.InvalidCredentials,
                    settings.AuthType == JiraAuthType.Cloud
                        ? "Jira rejected the email or API token. Check both, and make sure the token hasn't expired."
                        : "Jira rejected the Personal Access Token. Check it hasn't expired or been revoked."),
                HttpStatusCode.Forbidden => JiraConnectionResult.Failure(JiraConnectionError.Forbidden,
                    "The credentials are valid but this account isn't allowed to use the Jira API. Ask your Jira admin for access."),
                HttpStatusCode.NotFound => JiraConnectionResult.Failure(JiraConnectionError.NotJira,
                    "No Jira API was found at this URL. Check the URL and the Cloud / Server choice."),
                _ => JiraConnectionResult.Failure(JiraConnectionError.UnexpectedResponse,
                    $"Jira returned an unexpected response ({(int)response.StatusCode} {response.ReasonPhrase}).",
                    $"HTTP {(int)response.StatusCode}"),
            };
        }
    }

    private static JiraConnectionResult MapRequestException(HttpRequestException ex, Uri baseUrl)
    {
        if (ex.HttpRequestError == HttpRequestError.SecureConnectionError || ex.InnerException is AuthenticationException)
        {
            return JiraConnectionResult.Failure(JiraConnectionError.SslError,
                $"A secure connection to {baseUrl.Host} couldn't be made. Its SSL certificate may be invalid or untrusted.", ex.Message);
        }

        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => JiraConnectionResult.Failure(JiraConnectionError.HostUnreachable,
                $"Couldn't find {baseUrl.Host}. Check the URL for typos.", ex.Message),
            _ => JiraConnectionResult.Failure(JiraConnectionError.HostUnreachable,
                $"Couldn't reach {baseUrl.Host}. Check your network or VPN connection.", ex.Message),
        };
    }

    private static async Task<JiraConnectionResult> ParseUserAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // A login page or proxy can answer 200 with HTML, which means this isn't the Jira API.
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("displayName", out var displayName)
                && displayName.GetString() is { Length: > 0 } name)
            {
                var email = json.RootElement.TryGetProperty("emailAddress", out var e) ? e.GetString() : null;
                return JiraConnectionResult.Success(new JiraUser(name, email));
            }
        }
        catch (JsonException)
        {
        }

        return JiraConnectionResult.Failure(JiraConnectionError.NotJira,
            "This URL answered, but not like a Jira API. Check the URL and the Cloud / Server choice.");
    }
}

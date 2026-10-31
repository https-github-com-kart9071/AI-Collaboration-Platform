using System.Net.Http;
using AICollaborationPlatform.App.Models;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.App.Services;

public interface IJiraClientFactory
{
    IJiraClient Create(JiraConnectionSettings settings);
}

/// <summary>
/// Creates a <see cref="JiraClient"/> for a connection, using the "JiraApi" HttpClient
/// that has <see cref="JiraRetryHandler"/> in its pipeline.
/// </summary>
public sealed class JiraClientFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) : IJiraClientFactory
{
    public IJiraClient Create(JiraConnectionSettings settings) =>
        new JiraClient(httpClientFactory.CreateClient(JiraClient.HttpClientName), settings, loggerFactory.CreateLogger<JiraClient>());
}

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using AICollaborationPlatform.App.Models;

namespace AICollaborationPlatform.App.Services;

/// <summary>Builds authenticated Jira requests: Cloud uses Basic (email:token), Server uses Bearer (PAT).</summary>
public static class JiraAuth
{
    public static int ApiVersion(JiraConnectionSettings settings) => settings.AuthType == JiraAuthType.Cloud ? 3 : 2;

    public static HttpRequestMessage CreateRequest(JiraConnectionSettings settings, HttpMethod method, string pathAndQuery)
    {
        var request = new HttpRequestMessage(method, new Uri($"{settings.BaseUrl.AbsoluteUri.TrimEnd('/')}/{pathAndQuery.TrimStart('/')}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = settings.AuthType == JiraAuthType.Cloud
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Email}:{settings.Token}")))
            : new AuthenticationHeaderValue("Bearer", settings.Token);
        return request;
    }
}

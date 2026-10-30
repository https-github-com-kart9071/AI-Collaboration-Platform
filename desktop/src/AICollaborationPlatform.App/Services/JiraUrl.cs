namespace AICollaborationPlatform.App.Services;

/// <summary>
/// Turns whatever the user pasted into a clean Jira base URL.
/// </summary>
public static class JiraUrl
{
    // First path segments of Jira's own pages. Anything from here on is a page, not the base URL.
    private static readonly HashSet<string> JiraPageSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "browse", "secure", "projects", "issues", "plugins", "rest", "servicedesk", "login.jsp", "dashboards",
    };

    public static bool IsCloudHost(Uri url) =>
        url.Host.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Accepts only https URLs. Drops query string, fragment and trailing slash.
    /// For Jira Cloud the site root is always the base, so a pasted board or issue
    /// link like https://acme.atlassian.net/browse/ABC-1 becomes https://acme.atlassian.net.
    /// Server keeps any context path it's hosted under (e.g. /jira) but drops a pasted
    /// page path, so https://acme.com/jira/browse/ABC-1 becomes https://acme.com/jira.
    /// </summary>
    public static bool TryNormalize(string? input, out Uri? baseUrl, out string error)
    {
        baseUrl = null;
        var text = input?.Trim() ?? "";

        if (text.Length == 0)
        {
            error = "Enter your Jira URL.";
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || string.IsNullOrEmpty(url.Host))
        {
            error = "This isn't a valid URL. Example: https://your-company.atlassian.net";
            return false;
        }

        if (url.Scheme != Uri.UriSchemeHttps)
        {
            error = "The URL must start with https://";
            return false;
        }

        var path = IsCloudHost(url) ? "" : ContextPath(url);
        baseUrl = new Uri($"https://{url.Authority}{path}");
        error = "";
        return true;
    }

    private static string ContextPath(Uri url)
    {
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(segment => !JiraPageSegments.Contains(segment));
        var path = string.Join('/', segments);
        return path.Length == 0 ? "" : "/" + path;
    }
}

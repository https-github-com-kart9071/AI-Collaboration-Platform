namespace AICollaborationPlatform.App.Features.Code;

/// <summary>
/// Turns what the user typed or pasted into a clean server address (or, for Other, a repository URL).
/// </summary>
public static class CodeHostUrl
{
    /// <summary>
    /// Accepts https only (a token is sent to this address); adds https:// when no scheme was typed.
    /// Drops the query string, fragment and trailing slash.
    /// GitHub and GitLab servers are used from their root, so a pasted page like
    /// https://gitlab.acme.com/team/app/-/issues becomes https://gitlab.acme.com.
    /// Bitbucket and Azure DevOps keep the path, which can be part of the address
    /// (https://acme.com/bitbucket, https://acme.com/tfs/DefaultCollection). Other keeps the repository path.
    /// </summary>
    public static bool TryNormalize(CodeHostOption host, string? input, out Uri? address, out string error)
    {
        address = null;
        var text = input?.Trim() ?? "";
        var isRepository = host.Kind == CodeHostKind.Other;

        if (text.Length == 0)
        {
            error = isRepository ? "Enter the repository URL." : $"Enter your {host.SelfHostedName} address.";
            return false;
        }

        if (text.StartsWith("git@", StringComparison.OrdinalIgnoreCase) || text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            error = "SSH addresses aren't supported yet. Use the HTTPS address (https://…).";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || string.IsNullOrEmpty(url.Host) || url.Host.Contains(' '))
        {
            error = $"This isn't a valid address. Example: {host.ServerExample}";
            return false;
        }

        if (url.Scheme != Uri.UriSchemeHttps)
        {
            error = "The address must start with https://";
            return false;
        }

        if (!isRepository && IsCloudHost(host, url))
        {
            error = $"That's {host.CloudName}. Choose {host.CloudName} above instead of a self-hosted server.";
            return false;
        }

        var keepPath = host.Kind is CodeHostKind.Bitbucket or CodeHostKind.AzureDevOps or CodeHostKind.Other;
        var path = keepPath ? url.AbsolutePath.TrimEnd('/') : "";
        if (isRepository && path.Length == 0)
        {
            error = $"Enter the full repository URL, not just the server. Example: {host.ServerExample}";
            return false;
        }

        address = new Uri($"https://{url.Authority}{path}");
        error = "";
        return true;
    }

    private static bool IsCloudHost(CodeHostOption host, Uri url)
    {
        if (host.CloudUrl is null)
        {
            return false;
        }

        var cloud = new Uri(host.CloudUrl).Host;
        return url.Host.Equals(cloud, StringComparison.OrdinalIgnoreCase)
            || url.Host.Equals("www." + cloud, StringComparison.OrdinalIgnoreCase)
            || (host.Kind == CodeHostKind.AzureDevOps && url.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase));
    }
}

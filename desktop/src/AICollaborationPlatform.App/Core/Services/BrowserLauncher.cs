using System.Diagnostics;

namespace AICollaborationPlatform.App.Services;

public interface IBrowserLauncher
{
    void Open(Uri url);
}

/// <summary>Opens a web page in the user's default browser.</summary>
public sealed class BrowserLauncher : IBrowserLauncher
{
    public void Open(Uri url) => Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
}

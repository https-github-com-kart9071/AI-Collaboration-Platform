using System.IO;
using AICollaborationPlatform.App.Storage;

namespace AICollaborationPlatform.Tests;

public class AppPathsTests
{
    [Fact]
    public void Log_file_lives_under_appdata()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal(
            Path.Combine(appData, "AICollaborationPlatform", "logs", "app.log"),
            AppPaths.LogFile);
    }
}

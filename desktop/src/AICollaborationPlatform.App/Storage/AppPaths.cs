using System.IO;

namespace AICollaborationPlatform.App.Storage;

/// <summary>
/// Well-known locations for per-user app data under %APPDATA%\AICollaborationPlatform.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "AICollaborationPlatform";

    /// <summary>
    /// %APPDATA%\AICollaborationPlatform, or the folder in the AICOLLAB_DATA_DIR environment variable
    /// (handy for trying the app against test data without touching your real data).
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("AICOLLAB_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string LogsDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Base log file name. Serilog rolls daily, so the files on disk are app20261030.log etc.
    /// </summary>
    public static string LogFile { get; } = Path.Combine(LogsDirectory, "app.log");

    /// <summary>Non-secret settings. Secrets go to Windows Credential Manager, never here.</summary>
    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    /// <summary>Local copy of the fetched Jira data (SQLite).</summary>
    public static string DatabaseFile { get; } = Path.Combine(DataDirectory, "data.db");

    public static void EnsureCreated() => Directory.CreateDirectory(LogsDirectory);
}

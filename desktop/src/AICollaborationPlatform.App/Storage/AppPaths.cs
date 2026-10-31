using System.IO;

namespace AICollaborationPlatform.App.Storage;

/// <summary>
/// Well-known locations for per-user app data under %APPDATA%\AICollaborationPlatform.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "AICollaborationPlatform";

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName);

    public static string LogsDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Base log file name. Serilog rolls daily, so the files on disk are app20261030.log etc.
    /// </summary>
    public static string LogFile { get; } = Path.Combine(LogsDirectory, "app.log");

    /// <summary>Non-secret settings. Secrets go to Windows Credential Manager, never here.</summary>
    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    public static void EnsureCreated() => Directory.CreateDirectory(LogsDirectory);
}

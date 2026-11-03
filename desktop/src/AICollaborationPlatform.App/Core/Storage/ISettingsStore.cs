namespace AICollaborationPlatform.App.Storage;

public interface ISettingsStore
{
    /// <summary>Returns the saved settings, or empty settings if none are saved or the file is unreadable.</summary>
    AppSettings Load();

    void Save(AppSettings settings);
}

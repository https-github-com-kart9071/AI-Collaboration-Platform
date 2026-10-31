namespace AICollaborationPlatform.App.Storage;

/// <summary>
/// Stores secrets (tokens) outside the app's files, protected by the OS for the current Windows user.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Returns the secret, or null if nothing is stored under <paramref name="target"/>.</summary>
    string? Read(string target);

    /// <summary>Creates or replaces the secret stored under <paramref name="target"/>.</summary>
    void Write(string target, string userName, string secret);

    /// <summary>Removes the secret. Returns false if there was nothing to remove.</summary>
    bool Delete(string target);
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace AICollaborationPlatform.App.ViewModels;

/// <summary>
/// Base class for anything shown in the main content area.
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
}

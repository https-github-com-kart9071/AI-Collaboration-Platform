using CommunityToolkit.Mvvm.ComponentModel;

namespace AICollaborationPlatform.App.ViewModels;

/// <summary>
/// Base class for anything shown in the main content area.
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Called each time the page is shown, e.g. to reload data that may have changed.</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
}

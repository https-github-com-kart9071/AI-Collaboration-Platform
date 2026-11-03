using AICollaborationPlatform.App.ViewModels;

namespace AICollaborationPlatform.App.Services;

public interface INavigationService
{
    PageViewModel? CurrentPage { get; }

    event EventHandler? CurrentPageChanged;

    void NavigateTo<TPage>() where TPage : PageViewModel;

    void NavigateTo(Type pageViewModelType);
}

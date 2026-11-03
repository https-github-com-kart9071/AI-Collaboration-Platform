namespace AICollaborationPlatform.App.Shell;

public interface INavigationService
{
    PageViewModel? CurrentPage { get; }

    event EventHandler? CurrentPageChanged;

    void NavigateTo<TPage>() where TPage : PageViewModel;

    void NavigateTo(Type pageViewModelType);
}

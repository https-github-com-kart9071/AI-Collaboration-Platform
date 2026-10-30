using AICollaborationPlatform.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.App.Services;

/// <summary>
/// Resolves page view models from the DI container. The matching view is picked
/// by the DataTemplates in App.xaml, so pages never reference their views directly.
/// </summary>
public sealed class NavigationService(IServiceProvider services, ILogger<NavigationService> logger) : INavigationService
{
    public PageViewModel? CurrentPage { get; private set; }

    public event EventHandler? CurrentPageChanged;

    public void NavigateTo<TPage>() where TPage : PageViewModel => NavigateTo(typeof(TPage));

    public void NavigateTo(Type pageViewModelType)
    {
        if (!typeof(PageViewModel).IsAssignableFrom(pageViewModelType))
        {
            throw new ArgumentException($"{pageViewModelType.Name} is not a {nameof(PageViewModel)}.", nameof(pageViewModelType));
        }

        if (CurrentPage?.GetType() == pageViewModelType)
        {
            return;
        }

        CurrentPage = (PageViewModel)services.GetRequiredService(pageViewModelType);
        logger.LogInformation("Navigated to {Page}", CurrentPage.Title);
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }
}

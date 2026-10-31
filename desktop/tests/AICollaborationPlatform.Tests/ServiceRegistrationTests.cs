using System.Net;
using System.Net.Http;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.Tests;

/// <summary>Builds the app's real container (minus windows) to catch wiring mistakes.</summary>
public class ServiceRegistrationTests
{
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection().AddLogging();
        App.App.ConfigureServices(services);
        services.AddSingleton(TestData.TempDatabase()); // never touch the real %APPDATA% database
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void Core_services_and_view_models_resolve()
    {
        using var provider = BuildServices();

        Assert.NotNull(provider.GetRequiredService<IJiraSession>());
        Assert.NotNull(provider.GetRequiredService<IJiraClientFactory>());
        Assert.NotNull(provider.GetRequiredService<IJiraConnectionService>());
        Assert.NotNull(provider.GetRequiredService<JiraConnectionViewModel>());
        Assert.NotNull(provider.GetRequiredService<JiraViewModel>());
        Assert.NotNull(provider.GetRequiredService<IJiraSyncService>());
        Assert.NotNull(provider.GetRequiredService<JiraDataViewModel>());
        Assert.Same(provider.GetRequiredService<JiraViewModel>().Fetch, provider.GetRequiredService<JiraFetchViewModel>());
    }

    [Fact]
    public async Task Jira_api_client_retries_through_the_registered_pipeline()
    {
        var services = new ServiceCollection().AddLogging();
        App.App.ConfigureServices(services);

        // Swap the real network for a stub that is rate limited once.
        var calls = 0;
        services.Configure<HttpClientFactoryOptions>(JiraClient.HttpClientName, options =>
            options.HttpMessageHandlerBuilderActions.Add(builder =>
                builder.PrimaryHandler = new StubHttpHandler((_, _) => Task.FromResult(++calls == 1
                    ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.Zero) } }
                    : JiraApiStub.Json("[]")))));
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IJiraClientFactory>()
            .Create(new JiraConnectionSettings(new Uri("https://acme.atlassian.net"), JiraAuthType.Cloud, "a@b.com", "t"));
        var fields = await client.GetFieldsAsync();

        Assert.Empty(fields);
        Assert.Equal(2, calls);
    }
}

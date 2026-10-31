using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.Storage;
using AICollaborationPlatform.App.ViewModels;
using AICollaborationPlatform.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AICollaborationPlatform.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF formats bindings as en-US unless told otherwise; use the user's Windows date/number format.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        AppPaths.EnsureCreated();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                AppPaths.LogFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true, // lets a second running copy (e.g. one from Visual Studio) log too
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");

        _host = Host.CreateDefaultBuilder(e.Args)
            .UseSerilog()
            .ConfigureServices(ConfigureServices)
            .Build();

        await _host.StartAsync();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("AI Collaboration Platform starting. Logs: {LogsDirectory}", AppPaths.LogsDirectory);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

        // Reconnect to the saved Jira connection in the background; the Jira page shows progress.
        _ = _host.Services.GetRequiredService<IJiraSession>().RestoreAsync();
    }

    // Internal so tests can build the real container and check every registration resolves.
    internal static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();

        // Storage: secrets in Windows Credential Manager, everything else in settings.json
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<ISettingsStore>(sp =>
            new JsonSettingsStore(AppPaths.SettingsFile, sp.GetRequiredService<ILogger<JsonSettingsStore>>()));

        // Jira
        services.AddHttpClient<IJiraConnectionService, JiraConnectionService>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AICollaborationPlatform/0.1");
            })
            // JiraSession is a singleton holding this client for the app's lifetime;
            // recycling pooled connections keeps it picking up DNS changes.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        services.AddSingleton<IJiraSession, JiraSession>();

        // Jira data API: retries rate limits and server errors. The timeout covers all retries of one call.
        services.AddTransient<JiraRetryHandler>();
        services.AddHttpClient(JiraClient.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromMinutes(3);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AICollaborationPlatform/0.1");
            })
            .AddHttpMessageHandler<JiraRetryHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        services.AddSingleton<IJiraClientFactory, JiraClientFactory>();

        // Fetching Jira into the local SQLite database
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new JiraDatabaseFactory(AppPaths.DatabaseFile));
        services.AddSingleton<IJiraSyncService, JiraSyncService>();
        services.AddSingleton<JiraFetchViewModel>();

        // Browsing the local copy
        services.AddSingleton<IJiraDataQueries, JiraDataQueries>();
        services.AddSingleton<IBrowserLauncher, BrowserLauncher>();
        services.AddTransient<JiraConnectionViewModel>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        // Pages are singletons so their state survives switching between them.
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<JiraViewModel>();
        services.AddSingleton<JiraDataViewModel>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to the log in:\n{AppPaths.LogsDirectory}",
            "AI Collaboration Platform",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        Log.Information("AI Collaboration Platform exited with code {ExitCode}", e.ApplicationExitCode);
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}

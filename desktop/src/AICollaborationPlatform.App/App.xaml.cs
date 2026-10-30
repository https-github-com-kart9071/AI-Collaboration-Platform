using System.Windows;
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

        AppPaths.EnsureCreated();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                AppPaths.LogFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
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
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();

        // Jira
        services.AddHttpClient<IJiraConnectionService, JiraConnectionService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AICollaborationPlatform/0.1");
        });
        services.AddSingleton<IJiraSession, JiraSession>();
        services.AddTransient<JiraConnectionViewModel>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        // Pages are singletons so their state survives switching between them.
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<JiraViewModel>();
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

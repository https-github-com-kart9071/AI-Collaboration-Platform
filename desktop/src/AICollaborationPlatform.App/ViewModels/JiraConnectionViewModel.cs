using System.Net.Mail;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AICollaborationPlatform.App.ViewModels;

public enum ConnectionStatusKind
{
    None,
    Working,
    Success,
    Error,
}

/// <summary>
/// Backs the Jira connection dialog: collects the URL and credentials,
/// tests them against Jira, and stores a working connection in <see cref="IJiraSession"/>.
/// </summary>
public sealed partial class JiraConnectionViewModel : ObservableObject
{
    private readonly IJiraConnectionService _connectionService;
    private readonly IJiraSession _session;
    private CancellationTokenSource? _testCts;

    public JiraConnectionViewModel(IJiraConnectionService connectionService, IJiraSession session)
    {
        _connectionService = connectionService;
        _session = session;

        // Reopening the dialog while connected starts from the current connection.
        if (session.Settings is { } current)
        {
            Url = current.BaseUrl.AbsoluteUri.TrimEnd('/');
            AuthType = current.AuthType;
            Email = current.Email ?? "";
            Token = current.Token;
        }
    }

    /// <summary>Raised when the dialog should close. True means connected.</summary>
    public event EventHandler<bool>? RequestClose;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand), nameof(ConnectCommand))]
    private string _url = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloud), nameof(IsServer), nameof(TokenLabel), nameof(TokenHelpText), nameof(TokenHelpUrl))]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand), nameof(ConnectCommand))]
    private JiraAuthType _authType = JiraAuthType.Cloud;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand), nameof(ConnectCommand))]
    private string _email = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand), nameof(ConnectCommand))]
    private string _token = "";

    [ObservableProperty]
    private bool _isTokenVisible;

    [ObservableProperty]
    private string? _urlError;

    [ObservableProperty]
    private string? _emailError;

    [ObservableProperty]
    private string? _tokenError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand), nameof(ConnectCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private ConnectionStatusKind _statusKind;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string? _statusDetails;

    // Radio buttons bind to these two instead of the enum.
    public bool IsCloud
    {
        get => AuthType == JiraAuthType.Cloud;
        set { if (value) AuthType = JiraAuthType.Cloud; }
    }

    public bool IsServer
    {
        get => AuthType == JiraAuthType.Server;
        set { if (value) AuthType = JiraAuthType.Server; }
    }

    public string TokenLabel => IsCloud ? "API token" : "Personal Access Token";

    public string TokenHelpText => IsCloud ? "Create an API token" : "How to create a Personal Access Token";

    public string TokenHelpUrl => IsCloud
        ? "https://id.atlassian.com/manage-profile/security/api-tokens"
        : "https://confluence.atlassian.com/enterprise/using-personal-access-tokens-1026032365.html";

    partial void OnUrlChanged(string value)
    {
        UrlError = null;
        ClearStatus();

        // Atlassian-hosted sites are always Cloud.
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url) && JiraUrl.IsCloudHost(url))
        {
            AuthType = JiraAuthType.Cloud;
        }
    }

    partial void OnAuthTypeChanged(JiraAuthType value)
    {
        EmailError = null;
        TokenError = null;
        ClearStatus();
    }

    partial void OnEmailChanged(string value)
    {
        EmailError = null;
        ClearStatus();
    }

    partial void OnTokenChanged(string value)
    {
        TokenError = null;
        ClearStatus();
    }

    private bool CanTest() =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(Url)
        && !string.IsNullOrWhiteSpace(Token)
        && (IsServer || !string.IsNullOrWhiteSpace(Email));

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync() => await RunTestAsync();

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task ConnectAsync()
    {
        var (settings, result) = await RunTestAsync();
        if (settings is not null && result is { IsSuccess: true })
        {
            _session.Connect(settings, result.User!);
            RequestClose?.Invoke(this, true);
        }
    }

    /// <summary>Cancels a running test first; a second click closes the dialog.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_testCts is not null)
        {
            _testCts.Cancel();
            return;
        }

        RequestClose?.Invoke(this, false);
    }

    [RelayCommand]
    private void ToggleTokenVisibility() => IsTokenVisible = !IsTokenVisible;

    private async Task<(JiraConnectionSettings? Settings, JiraConnectionResult? Result)> RunTestAsync()
    {
        var settings = Validate();
        if (settings is null)
        {
            return (null, null);
        }

        IsBusy = true;
        SetStatus(ConnectionStatusKind.Working, "Connecting to Jira…");
        _testCts = new CancellationTokenSource();

        try
        {
            var result = await _connectionService.TestConnectionAsync(settings, _testCts.Token);
            SetStatus(result.IsSuccess ? ConnectionStatusKind.Success : ConnectionStatusKind.Error, result.Message, result.Details);
            return (settings, result);
        }
        catch (OperationCanceledException)
        {
            SetStatus(ConnectionStatusKind.None, "Connection test cancelled.");
            return (settings, null);
        }
        finally
        {
            _testCts.Dispose();
            _testCts = null;
            IsBusy = false;
        }
    }

    /// <summary>Shows field errors and returns null if anything is invalid.</summary>
    private JiraConnectionSettings? Validate()
    {
        if (JiraUrl.TryNormalize(Url, out var baseUrl, out var urlError))
        {
            // Show the cleaned-up URL so the user sees exactly what is used.
            Url = baseUrl!.AbsoluteUri.TrimEnd('/');
        }
        else
        {
            UrlError = urlError;
        }

        var email = Email.Trim();
        EmailError = IsCloud && !MailAddress.TryCreate(email, out _)
            ? "Enter the email address you use to sign in to Jira."
            : null;

        var token = Token.Trim();
        TokenError = token.Length == 0 ? $"Enter your {TokenLabel}." : null;

        if (UrlError is not null || EmailError is not null || TokenError is not null)
        {
            ClearStatus();
            return null;
        }

        return new JiraConnectionSettings(baseUrl!, AuthType, IsCloud ? email : null, token);
    }

    private void SetStatus(ConnectionStatusKind kind, string message, string? details = null)
    {
        StatusKind = kind;
        StatusMessage = message;
        StatusDetails = details;
    }

    private void ClearStatus()
    {
        if (!IsBusy)
        {
            SetStatus(ConnectionStatusKind.None, "");
        }
    }
}

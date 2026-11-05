using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AICollaborationPlatform.App.Features.Code;

/// <summary>
/// The Connect dialog for code hosts, in two steps:
/// 1. the provider, then its cloud service or a self-hosted server address;
/// 2. signing in with a personal access token (plus a username where the host needs one).
/// Testing the token and saving the connection come next.
/// </summary>
public sealed partial class CodeConnectionViewModel : ObservableObject
{
    public IReadOnlyList<CodeHostOption> Hosts { get; } = CodeHostOption.All;

    /// <summary>GitHub by default, as the most common choice. Never null: the list always keeps one picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCloud), nameof(ShowServerAddress), nameof(ServerLabel), nameof(ServerExample))]
    private CodeHostOption _selectedHost = CodeHostOption.All[0];

    /// <summary>A company server instead of the provider's own cloud. Other is always "self-hosted".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloud), nameof(ShowServerAddress))]
    private bool _isSelfHosted;

    [ObservableProperty]
    private string _serverAddress = "";

    [ObservableProperty]
    private string? _serverAddressError;

    /// <summary>Step 2: shown after Continue has checked the address.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosingHost), nameof(SignInTitle), nameof(SignInServer), nameof(ShowUsername))]
    private bool _isSigningIn;

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string? _usernameError;

    /// <summary>Held in memory only; never logged. Saved to Credential Manager once the connection is made.</summary>
    [ObservableProperty]
    private string _token = "";

    [ObservableProperty]
    private string? _tokenError;

    [ObservableProperty]
    private bool _isTokenVisible;

    /// <summary>Shown under the sign-in form after Sign in; empty when there's nothing to say.</summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>Raised to close the dialog; true means connected.</summary>
    public event EventHandler<bool>? RequestClose;

    /// <summary>Where the app will connect, once Continue has checked it.</summary>
    public Uri? Server { get; private set; }

    /// <summary>The token's name, help link and permissions for the chosen host. Set by Continue.</summary>
    public CodeHostSignIn? SignIn { get; private set; }

    // ---- Step 1: provider and address ----

    public bool IsChoosingHost => !IsSigningIn;

    public bool HasCloud => SelectedHost.HasCloud;

    /// <summary>Two-way bound to the "cloud" radio button, the opposite of <see cref="IsSelfHosted"/>.</summary>
    public bool IsCloud
    {
        get => !IsSelfHosted;
        set => IsSelfHosted = !value;
    }

    public bool ShowServerAddress => IsSelfHosted || !HasCloud;

    public string ServerLabel => HasCloud ? $"{SelectedHost.SelfHostedName} address" : "Repository URL";

    public string ServerExample => $"For example: {SelectedHost.ServerExample}";

    // ---- Step 2: sign in ----

    public string SignInTitle => $"Sign in to {SelectedHost.Name}";

    public string SignInServer => Server?.AbsoluteUri.TrimEnd('/') ?? "";

    public bool ShowUsername => SignIn?.UsernameLabel is not null;

    partial void OnSelectedHostChanged(CodeHostOption? oldValue, CodeHostOption newValue)
    {
        // A list box can briefly clear its selection; keep the last choice instead of having none.
        if (newValue is null)
        {
            SelectedHost = oldValue ?? Hosts[0];
            return;
        }

        ServerAddressError = null;
    }

    partial void OnIsSelfHostedChanged(bool value) => ServerAddressError = null;

    partial void OnServerAddressChanged(string value) => ServerAddressError = null;

    partial void OnUsernameChanged(string value)
    {
        UsernameError = null;
        StatusMessage = "";
    }

    partial void OnTokenChanged(string value)
    {
        TokenError = null;
        StatusMessage = "";
    }

    [RelayCommand]
    private void Continue()
    {
        Uri server;
        if (!ShowServerAddress)
        {
            server = new Uri(SelectedHost.CloudUrl!);
        }
        else if (CodeHostUrl.TryNormalize(SelectedHost, ServerAddress, out var address, out var error))
        {
            ServerAddress = address!.AbsoluteUri.TrimEnd('/'); // show exactly what will be used
            server = address;
        }
        else
        {
            ServerAddressError = error;
            return;
        }

        Server = server;
        SignIn = CodeHostSignIn.For(SelectedHost, server);
        OnPropertyChanged(nameof(SignIn));
        IsSigningIn = true;
    }

    /// <summary>Back to the provider and address. What was typed on the sign-in step is kept.</summary>
    [RelayCommand]
    private void Back()
    {
        IsSigningIn = false;
        StatusMessage = "";
    }

    [RelayCommand]
    private void ToggleTokenVisibility() => IsTokenVisible = !IsTokenVisible;

    [RelayCommand]
    private void SignInToHost()
    {
        UsernameError = ShowUsername && string.IsNullOrWhiteSpace(Username) ? $"{SignIn!.UsernameLabel} is required." : null;
        TokenError = string.IsNullOrWhiteSpace(Token) ? $"{SignIn!.TokenLabel} is required." : null;
        if (UsernameError is not null || TokenError is not null)
        {
            return;
        }

        // Checking the token with the host (Test connection) replaces this message in the next part of Story 8.
        StatusMessage = $"Checking the token with {SelectedHost.Name} is coming next.";
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using AICollaborationPlatform.App.UI;

namespace AICollaborationPlatform.App.Features.Code;

public partial class CodeConnectionDialog : Window
{
    private readonly CodeConnectionViewModel _viewModel;
    private bool _syncingToken;

    public CodeConnectionDialog(CodeConnectionViewModel viewModel)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        TokenBox.Password = viewModel.Token;
        viewModel.RequestClose += OnRequestClose;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Picking "self-hosted" means typing an address next, so put the cursor there.
            // (Not when arrowing to Other in the list: that would pull focus out of the list.)
            case nameof(CodeConnectionViewModel.IsSelfHosted) when _viewModel.IsSelfHosted:
                FocusWhenShown(ServerBox);
                break;

            case nameof(CodeConnectionViewModel.IsSigningIn):
                FocusWhenShown(_viewModel.IsSigningIn ? (_viewModel.ShowUsername ? UsernameBox : TokenBox) : HostList);
                break;

            // PasswordBox.Password isn't a bindable property, so mirror it by hand.
            case nameof(CodeConnectionViewModel.Token) when TokenBox.Password != _viewModel.Token:
                _syncingToken = true;
                TokenBox.Password = _viewModel.Token;
                _syncingToken = false;
                break;
        }
    }

    // After the element becomes visible; focusing a collapsed element does nothing.
    private void FocusWhenShown(UIElement element) =>
        Dispatcher.BeginInvoke(() => element.Focus(), DispatcherPriority.Input);

    private void OnTokenBoxPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingToken)
        {
            _viewModel.Token = TokenBox.Password;
        }
    }

    // The link is built from the server address the user typed, so only ever open it in the browser.
    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme == Uri.UriSchemeHttps)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }

        e.Handled = true;
    }

    private void OnRequestClose(object? sender, bool connected) => DialogResult = connected;

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnClosed(e);
    }
}

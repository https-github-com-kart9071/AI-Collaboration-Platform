using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using AICollaborationPlatform.App.ViewModels;

namespace AICollaborationPlatform.App.Views;

public partial class JiraConnectionDialog : Window
{
    private readonly JiraConnectionViewModel _viewModel;
    private bool _syncingToken;

    public JiraConnectionDialog(JiraConnectionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        TokenBox.Password = viewModel.Token;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, bool connected) => DialogResult = connected;

    // PasswordBox.Password isn't a bindable property, so mirror it both ways by hand.
    private void OnTokenBoxPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingToken)
        {
            _viewModel.Token = TokenBox.Password;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JiraConnectionViewModel.Token) && TokenBox.Password != _viewModel.Token)
        {
            _syncingToken = true;
            TokenBox.Password = _viewModel.Token;
            _syncingToken = false;
        }
    }

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing with the X during a test must not connect when the test finishes later.
        if (_viewModel.IsBusy)
        {
            _viewModel.CancelCommand.Execute(null);
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.RequestClose -= OnRequestClose;
        base.OnClosed(e);
    }
}

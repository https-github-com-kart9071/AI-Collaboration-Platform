using System.Windows;
using AICollaborationPlatform.App.ViewModels;

namespace AICollaborationPlatform.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

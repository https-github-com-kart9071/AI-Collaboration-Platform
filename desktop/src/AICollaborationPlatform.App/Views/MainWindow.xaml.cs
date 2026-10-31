using System.Windows;
using AICollaborationPlatform.App.ViewModels;

namespace AICollaborationPlatform.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Open at most of the screen: the data table needs the room. Never larger than the screen itself.
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(1600, area.Width * 0.85));
        Height = Math.Max(MinHeight, Math.Min(1000, area.Height * 0.85));
    }
}

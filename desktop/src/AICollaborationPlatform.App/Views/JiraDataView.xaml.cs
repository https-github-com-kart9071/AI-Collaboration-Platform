using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.ViewModels;

namespace AICollaborationPlatform.App.Views;

/// <summary>
/// The view model does the sorting (fast, on plain lists); this code-behind only turns header clicks,
/// double-clicks and keys into commands, and shows the sort arrow on the right column.
/// </summary>
public partial class JiraDataView : UserControl
{
    private JiraDataViewModel? _viewModel;

    public JiraDataView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as JiraDataViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateSortArrows();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JiraDataViewModel.SortColumn) or nameof(JiraDataViewModel.SortDescending) or nameof(JiraDataViewModel.Issues))
        {
            UpdateSortArrows();
        }
    }

    // Replace the DataGrid's built-in (slow, reflection-based) sorting with the view model's.
    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        _viewModel?.SortCommand.Execute(e.Column.SortMemberPath);
    }

    private void UpdateSortArrows()
    {
        foreach (var column in IssuesGrid.Columns)
        {
            column.SortDirection = _viewModel is not null && column.SortMemberPath == _viewModel.SortColumn
                ? _viewModel.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending
                : null;
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only rows open details; double-clicking a header or the scrollbar shouldn't.
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { Item: IssueRow row })
        {
            _viewModel?.OpenIssueCommand.Execute(row);
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && IssuesGrid.SelectedItem is IssueRow row)
        {
            e.Handled = true; // stop the grid moving to the next row
            _viewModel?.OpenIssueCommand.Execute(row);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel?.IsDetailOpen == true)
        {
            e.Handled = true;
            _viewModel.CloseDetailCommand.Execute(null);
            IssuesGrid.Focus();
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    // The read-only text boxes in the issue panel swallow the mouse wheel even when they have nothing to scroll,
    // so the panel scrolls itself before they see the event.
    private void OnDetailMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scroller = (ScrollViewer)sender;
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return element as T;
    }
}

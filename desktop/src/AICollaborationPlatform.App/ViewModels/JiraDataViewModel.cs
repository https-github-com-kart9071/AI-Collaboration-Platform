using System.Collections.ObjectModel;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.App.ViewModels;

/// <summary>A choice in a filter drop-down. <see cref="Value"/> null means "no filter".</summary>
public sealed record FilterOption(string Label, string? Value);

/// <summary>An entry in the project list. <see cref="Key"/> null means "all projects".</summary>
public sealed record ProjectOption(string? Key, string Label, int IssueCount);

/// <summary>
/// The Jira data browser: projects on the left, a filterable, sortable issue table, and a detail panel.
/// All issues are held in memory and filtered/sorted with LINQ, then handed to the (virtualized) table
/// as a plain list, which stays fast with tens of thousands of rows.
/// </summary>
public sealed partial class JiraDataViewModel : PageViewModel
{
    public const string UnassignedLabel = "Unassigned";

    private readonly IJiraDataQueries _queries;
    private readonly IBrowserLauncher _browser;
    private readonly INavigationService _navigation;
    private readonly ILogger<JiraDataViewModel> _logger;

    private JiraDataSnapshot _snapshot = JiraDataSnapshot.Empty;
    private CancellationTokenSource? _searchDebounce;
    private int _loadVersion;
    private bool _suppressFilters;

    public JiraDataViewModel(IJiraDataQueries queries, IBrowserLauncher browser, INavigationService navigation, ILogger<JiraDataViewModel> logger)
    {
        _queries = queries;
        _browser = browser;
        _navigation = navigation;
        _logger = logger;
    }

    public override string Title => "Jira data";

    /// <summary>How long to wait after typing before searching. Tests set it to zero.</summary>
    internal TimeSpan SearchDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    // ---- Data shown ----

    public ObservableCollection<ProjectOption> Projects { get; } = [];

    [ObservableProperty]
    private IReadOnlyList<IssueRow> _issues = [];

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasData;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    public bool IsEmpty => !HasData && !IsLoading;

    public string? Site => _snapshot.Site;

    // ---- Filters ----

    [ObservableProperty]
    private ProjectOption? _selectedProject;

    [ObservableProperty]
    private IReadOnlyList<FilterOption> _statusOptions = [];

    [ObservableProperty]
    private FilterOption? _selectedStatus;

    [ObservableProperty]
    private IReadOnlyList<FilterOption> _assigneeOptions = [];

    [ObservableProperty]
    private FilterOption? _selectedAssignee;

    [ObservableProperty]
    private IReadOnlyList<FilterOption> _typeOptions = [];

    [ObservableProperty]
    private FilterOption? _selectedType;

    [ObservableProperty]
    private string _searchText = "";

    // ---- Sorting (the view mirrors these as column header arrows) ----

    [ObservableProperty]
    private string _sortColumn = nameof(IssueRow.Updated);

    [ObservableProperty]
    private bool _sortDescending = true;

    // ---- Detail panel ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInJiraCommand))]
    private IssueDetail? _detail;

    [ObservableProperty]
    private bool _isDetailOpen;

    [ObservableProperty]
    private bool _isDetailLoading;

    [ObservableProperty]
    private int _detailTabIndex;

    [ObservableProperty]
    private IssueRow? _selectedIssue;

    public override Task OnNavigatedToAsync() => LoadAsync();

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>Reloads everything from the database, keeping the current filters where they still apply.</summary>
    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            var snapshot = await Task.Run(_queries.LoadSnapshot);
            if (version != _loadVersion)
            {
                return; // a newer load started meanwhile
            }

            _snapshot = snapshot;
            OnPropertyChanged(nameof(Site));
            OpenInJiraCommand.NotifyCanExecuteChanged();
            HasData = snapshot.Issues.Count > 0;
            RebuildProjects();
            RebuildFilterOptions();
            ApplyFilters();

            // Keep an open detail panel in sync, or close it if the issue is gone.
            if (IsDetailOpen && Detail is { } open)
            {
                var row = snapshot.Issues.FirstOrDefault(i => i.Id == open.Id);
                if (row is null) CloseDetail(); else await OpenIssueAsync(row);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Couldn't load Jira data");
            HasData = false;
            CountText = $"Couldn't read the local data: {ex.Message}";
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    partial void OnSelectedProjectChanged(ProjectOption? value)
    {
        if (_suppressFilters) return;
        RebuildFilterOptions(); // the drop-downs list only values found in the chosen project
        ApplyFilters();
    }

    partial void OnSelectedStatusChanged(FilterOption? value) => ApplyFiltersUnlessSuppressed();

    partial void OnSelectedAssigneeChanged(FilterOption? value) => ApplyFiltersUnlessSuppressed();

    partial void OnSelectedTypeChanged(FilterOption? value) => ApplyFiltersUnlessSuppressed();

    partial void OnSearchTextChanged(string value)
    {
        if (!_suppressFilters) _ = SearchAfterPauseAsync();
    }

    private void ApplyFiltersUnlessSuppressed()
    {
        if (!_suppressFilters) ApplyFilters();
    }

    // Typing fires a change per key; wait for a short pause so the table isn't rebuilt on every letter.
    private async Task SearchAfterPauseAsync()
    {
        _searchDebounce?.Cancel();
        if (SearchDelay <= TimeSpan.Zero)
        {
            ApplyFilters();
            return;
        }

        var cts = _searchDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(SearchDelay, cts.Token);
            ApplyFilters();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressFilters = true;
        SelectedStatus = StatusOptions.FirstOrDefault();
        SelectedAssignee = AssigneeOptions.FirstOrDefault();
        SelectedType = TypeOptions.FirstOrDefault();
        _searchDebounce?.Cancel();
        SearchText = "";
        _suppressFilters = false;
        ApplyFilters();
    }

    /// <summary>Column header click: same column flips direction; a new column starts ascending (dates start newest first).</summary>
    [RelayCommand]
    private void Sort(string? column)
    {
        if (string.IsNullOrEmpty(column)) return;

        if (column == SortColumn)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = column is nameof(IssueRow.Created) or nameof(IssueRow.Updated);
        }

        ApplyFilters();
    }

    [RelayCommand]
    private async Task OpenIssueAsync(IssueRow? row)
    {
        if (row is null) return;

        var reopeningSame = IsDetailOpen && Detail?.Id == row.Id;
        IsDetailOpen = true;
        IsDetailLoading = true;
        try
        {
            Detail = await Task.Run(() => _queries.LoadIssueDetail(row.Id));
            if (!reopeningSame) DetailTabIndex = 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Couldn't load issue {Key}", row.Key);
            Detail = null;
        }
        finally
        {
            IsDetailLoading = false;
        }
    }

    [RelayCommand]
    private void CloseDetail()
    {
        IsDetailOpen = false;
        Detail = null;
    }

    private bool CanOpenInJira(string? issueKey) => !string.IsNullOrEmpty(Site) && !string.IsNullOrEmpty(issueKey ?? Detail?.Key);

    [RelayCommand(CanExecute = nameof(CanOpenInJira))]
    private void OpenInJira(string? issueKey)
    {
        var key = issueKey ?? Detail?.Key;
        if (Site is null || key is null) return;

        var url = new Uri($"{Site.TrimEnd('/')}/browse/{Uri.EscapeDataString(key)}");
        try
        {
            _browser.Open(url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't open {Url}", url);
        }
    }

    [RelayCommand]
    private void GoToJiraPage() => _navigation.NavigateTo<JiraViewModel>();

    // ---- Filtering and sorting ----

    private IEnumerable<IssueRow> IssuesInSelectedProject =>
        SelectedProject?.Key is { } key ? _snapshot.Issues.Where(i => i.ProjectKey == key) : _snapshot.Issues;

    /// <summary>Runs the filters, search and sort over the in-memory issues and swaps in the result.</summary>
    public void ApplyFilters()
    {
        var query = IssuesInSelectedProject;

        if (SelectedStatus?.Value is { } status) query = query.Where(i => i.Status == status);
        if (SelectedType?.Value is { } type) query = query.Where(i => i.Type == type);
        if (SelectedAssignee?.Value is { } assignee)
        {
            query = assignee == UnassignedLabel ? query.Where(i => i.Assignee is null) : query.Where(i => i.Assignee == assignee);
        }

        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            query = query.Where(i =>
                i.Key.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                i.Summary.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var result = Sorted(query).ToList();
        Issues = result;

        var total = SelectedProject?.Key is null ? _snapshot.Issues.Count : SelectedProject.IssueCount;
        CountText = result.Count == total
            ? $"{total:N0} issue{(total == 1 ? "" : "s")}"
            : $"Showing {result.Count:N0} of {total:N0} issues";

        if (SelectedIssue is not null && !result.Contains(SelectedIssue))
        {
            SelectedIssue = null;
        }
    }

    private IEnumerable<IssueRow> Sorted(IEnumerable<IssueRow> rows)
    {
        var text = StringComparer.CurrentCultureIgnoreCase;

        // Empty values always go last, whichever direction.
        IOrderedEnumerable<IssueRow> By<T>(Func<IssueRow, T> key, IComparer<T>? comparer = null)
        {
            var withValues = rows.OrderBy(r => key(r) is null);
            return SortDescending ? withValues.ThenByDescending(key, comparer) : withValues.ThenBy(key, comparer);
        }

        var sorted = SortColumn switch
        {
            nameof(IssueRow.Summary) => By(r => r.Summary, text),
            nameof(IssueRow.Type) => By(r => r.Type, text),
            nameof(IssueRow.Status) => By(r => r.Status, text),
            nameof(IssueRow.Assignee) => By(r => r.Assignee, text),
            nameof(IssueRow.Priority) => By(r => r.Priority, text),
            nameof(IssueRow.Created) => By(r => r.Created),
            nameof(IssueRow.Updated) => By(r => r.Updated),
            _ => SortDescending
                ? rows.OrderByDescending(r => r.ProjectKey, text).ThenByDescending(r => r.KeyNumber)
                : rows.OrderBy(r => r.ProjectKey, text).ThenBy(r => r.KeyNumber),
        };

        // Ties keep a stable, natural key order.
        return SortColumn == nameof(IssueRow.Key) ? sorted : sorted.ThenBy(r => r.ProjectKey, text).ThenBy(r => r.KeyNumber);
    }

    private void RebuildProjects()
    {
        var previous = SelectedProject?.Key;
        _suppressFilters = true;

        Projects.Clear();
        Projects.Add(new ProjectOption(null, "All projects", _snapshot.Issues.Count));
        foreach (var p in _snapshot.Projects)
        {
            Projects.Add(new ProjectOption(p.Key, $"{p.Key} – {p.Name}", p.IssueCount));
        }

        SelectedProject = Projects.FirstOrDefault(p => p.Key == previous) ?? Projects[0];
        _suppressFilters = false;
    }

    private void RebuildFilterOptions()
    {
        var rows = IssuesInSelectedProject.ToList();
        _suppressFilters = true;

        StatusOptions = Options("All statuses", rows.Select(r => r.Status));
        TypeOptions = Options("All types", rows.Select(r => r.Type));
        AssigneeOptions = Options("All assignees", rows.Select(r => r.Assignee ?? UnassignedLabel));

        // Keep each current choice if it still exists in the new list.
        SelectedStatus = Keep(StatusOptions, SelectedStatus);
        SelectedType = Keep(TypeOptions, SelectedType);
        SelectedAssignee = Keep(AssigneeOptions, SelectedAssignee);
        _suppressFilters = false;

        static IReadOnlyList<FilterOption> Options(string allLabel, IEnumerable<string?> values) =>
            [new FilterOption(allLabel, null),
             .. values.OfType<string>()
                 .GroupBy(v => v)
                 .OrderBy(g => g.Key == UnassignedLabel).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                 .Select(g => new FilterOption($"{g.Key} ({g.Count():N0})", g.Key))];

        static FilterOption Keep(IReadOnlyList<FilterOption> options, FilterOption? current) =>
            options.FirstOrDefault(o => o.Value == current?.Value) ?? options[0];
    }
}

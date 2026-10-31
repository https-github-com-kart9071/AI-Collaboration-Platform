using System.Collections.ObjectModel;
using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.App.ViewModels;

public enum FetchResultKind
{
    None,
    Success,
    Warning,
    Error,
}

/// <summary>A project in the "selected projects" list.</summary>
public sealed partial class SelectableProject(JiraProject project) : ObservableObject
{
    public string Key => project.Key;

    public string Name => project.Name;

    public string Label => $"{project.Key} – {project.Name}";

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// The "Fetch data" card on the Jira page: choose projects and an optional filter,
/// run the fetch with progress and cancel, and show what's stored locally.
/// </summary>
public sealed partial class JiraFetchViewModel : ObservableObject
{
    private readonly IJiraSyncService _sync;
    private readonly IJiraSession _session;
    private readonly ILogger<JiraFetchViewModel> _logger;
    private CancellationTokenSource? _fetchCts;
    private string? _projectsSite;

    public JiraFetchViewModel(IJiraSyncService sync, IJiraSession session, ILogger<JiraFetchViewModel> logger)
    {
        _sync = sync;
        _session = session;
        _logger = logger;

        _session.Changed += (_, _) =>
        {
            if (!_session.IsConnected)
            {
                _fetchCts?.Cancel(); // a disconnect stops a running fetch
            }

            FetchCommand.NotifyCanExecuteChanged();
        };

        RefreshSummary();
    }

    public ObservableCollection<SelectableProject> Projects { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedProjects))]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    private bool _isAllProjects = true;

    public bool IsSelectedProjects
    {
        get => !IsAllProjects;
        set => IsAllProjects = !value;
    }

    [ObservableProperty]
    private bool _isLoadingProjects;

    [ObservableProperty]
    private string? _projectsError;

    [ObservableProperty]
    private string _jqlFilter = "";

    [ObservableProperty]
    private bool _fullRefresh;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand), nameof(CancelCommand), nameof(LoadProjectsCommand))]
    private bool _isFetching;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private FetchResultKind _resultKind;

    [ObservableProperty]
    private string _resultMessage = "";

    public ObservableCollection<string> ResultWarnings { get; } = [];

    [ObservableProperty]
    private string _localDataText = "";

    public string SelectedCountText => $"{Projects.Count(p => p.IsSelected)} of {Projects.Count} selected";

    partial void OnIsAllProjectsChanged(bool value)
    {
        // Load the project list the first time "Selected projects" is chosen, or after switching sites.
        if (!value && (_projectsSite != CurrentSite || Projects.Count == 0) && !IsLoadingProjects && CanLoadProjects())
        {
            _ = LoadProjectsAsync();
        }
    }

    private string? CurrentSite => _session.Settings?.BaseUrl.AbsoluteUri;

    private bool CanFetch() =>
        !IsFetching && _session.IsConnected && (IsAllProjects || Projects.Any(p => p.IsSelected));

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchAsync()
    {
        var options = new JiraSyncOptions(
            IsAllProjects ? null : Projects.Where(p => p.IsSelected).Select(p => p.Key).ToList(),
            JqlFilter,
            FullRefresh);

        IsFetching = true;
        ResultKind = FetchResultKind.None;
        ResultMessage = "";
        ResultWarnings.Clear();
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressText = "Starting…";
        _fetchCts = new CancellationTokenSource();

        // Progress<T> captures the UI thread here, so updates from the background fetch arrive on it.
        var progress = new Progress<JiraSyncProgress>(OnProgress);
        try
        {
            // The database work is synchronous, so run the whole fetch off the UI thread.
            var token = _fetchCts.Token;
            var result = await Task.Run(() => _sync.SyncAsync(options, progress, token));
            ShowResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fetch failed unexpectedly");
            ResultKind = FetchResultKind.Error;
            ResultMessage = $"The fetch couldn't run: {ex.Message}";
        }
        finally
        {
            _fetchCts.Dispose();
            _fetchCts = null;
            IsFetching = false;
            ProgressText = "";
            RefreshSummary();
        }
    }

    [RelayCommand(CanExecute = nameof(IsFetching))]
    private void Cancel()
    {
        _fetchCts?.Cancel();
        ProgressText = "Cancelling…";
    }

    [RelayCommand(CanExecute = nameof(CanLoadProjects))]
    private async Task LoadProjectsAsync()
    {
        IsLoadingProjects = true;
        ProjectsError = null;
        try
        {
            var previouslySelected = Projects.Where(p => p.IsSelected).Select(p => p.Key).ToHashSet();
            var projects = await _sync.GetProjectsAsync();

            foreach (var old in Projects)
            {
                old.PropertyChanged -= OnProjectSelectionChanged;
            }

            Projects.Clear();
            foreach (var project in projects.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var item = new SelectableProject(project) { IsSelected = previouslySelected.Contains(project.Key) };
                item.PropertyChanged += OnProjectSelectionChanged;
                Projects.Add(item);
            }

            _projectsSite = CurrentSite;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't load projects");
            ProjectsError = $"Couldn't load projects: {ex.Message}";
        }
        finally
        {
            IsLoadingProjects = false;
            OnPropertyChanged(nameof(SelectedCountText));
            FetchCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanLoadProjects() => !IsFetching && _session.IsConnected;

    [RelayCommand]
    private void SelectAllProjects() => SetAllSelected(true);

    [RelayCommand]
    private void SelectNoProjects() => SetAllSelected(false);

    private void SetAllSelected(bool selected)
    {
        foreach (var project in Projects)
        {
            project.IsSelected = selected;
        }
    }

    private void OnProjectSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectedCountText));
        FetchCommand.NotifyCanExecuteChanged();
    }

    private void OnProgress(JiraSyncProgress p)
    {
        if (!IsFetching || _fetchCts?.IsCancellationRequested == true)
        {
            return;
        }

        ProgressText = p.Phase switch
        {
            JiraSyncPhase.Fields => "Reading Jira fields…",
            JiraSyncPhase.Projects => "Reading projects…",
            JiraSyncPhase.Users => "Reading users…",
            JiraSyncPhase.Issues when p.IssuesTotal is { } total =>
                $"Project {p.ProjectIndex} of {p.ProjectCount} · {p.ProjectKey} — Issues {p.IssuesDone:N0} of {Math.Max(total, p.IssuesDone):N0}",
            JiraSyncPhase.Issues => $"Project {p.ProjectIndex} of {p.ProjectCount} · {p.ProjectKey} — Issues {p.IssuesDone:N0}",
            JiraSyncPhase.Boards => $"Project {p.ProjectIndex} of {p.ProjectCount} · {p.ProjectKey} — boards and sprints",
            _ => "Starting…",
        };

        if (p.ProjectCount == 0)
        {
            IsProgressIndeterminate = true;
            return;
        }

        // Each project is an equal slice of the bar; issues fill their project's slice.
        var withinProject = p.Phase == JiraSyncPhase.Boards ? 1.0
            : p.IssuesTotal is > 0 and var t ? Math.Min(1.0, p.IssuesDone / (double)t)
            : 0.0;
        IsProgressIndeterminate = false;
        ProgressValue = Math.Max(0, (p.ProjectIndex - 1 + withinProject) / p.ProjectCount * 100);
    }

    private void ShowResult(JiraSyncResult result)
    {
        foreach (var warning in result.Warnings)
        {
            ResultWarnings.Add(warning);
        }

        (ResultKind, ResultMessage) = result.Status switch
        {
            JiraSyncStatus.Completed when result.Projects > 0 && result.Issues == 0 && result.IssuesRemoved == 0 =>
                (Kind(result), $"Everything is up to date: no issues changed since the last fetch ({result.Projects} projects checked in {FormatDuration(result.Duration)})."),
            JiraSyncStatus.Completed =>
                (Kind(result), $"Fetched {Plural(result.Projects, "project")}, {Plural(result.Issues, "issue")} and {Plural(result.Comments, "comment")} in {FormatDuration(result.Duration)}."
                    + (result.IssuesRemoved > 0 ? $" Removed {Plural(result.IssuesRemoved, "issue")} deleted in Jira." : "")),
            JiraSyncStatus.Cancelled =>
                (FetchResultKind.Warning, $"Fetch cancelled after {Plural(result.Issues, "issue")}. Everything saved so far is kept; the next fetch picks up the rest."),
            _ =>
                (FetchResultKind.Error, $"Fetch stopped: {result.Error} Data fetched before the error is kept."),
        };

        static FetchResultKind Kind(JiraSyncResult r) => r.Warnings.Count > 0 ? FetchResultKind.Warning : FetchResultKind.Success;
    }

    private void RefreshSummary()
    {
        try
        {
            var s = _sync.GetSummary();
            LocalDataText = s.LastFetchedAt is { } at
                ? $"{Plural(s.Projects, "project")} · {Plural(s.Issues, "issue")} · {Plural(s.Comments, "comment")} · {Plural(s.Sprints, "sprint")} · last fetched {at.ToLocalTime():d MMM yyyy, HH:mm}"
                : s.Issues > 0
                    ? $"{Plural(s.Issues, "issue")} from an unfinished fetch"
                    : "Nothing fetched yet";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't read the local database summary");
            LocalDataText = "Couldn't read the local data.";
        }
    }

    public static string FormatDuration(TimeSpan d) => d.TotalSeconds switch
    {
        < 1 => "less than a second",
        < 60 => $"{(int)d.TotalSeconds} s",
        < 3600 => $"{(int)d.TotalMinutes} min {d.Seconds} s",
        _ => $"{(int)d.TotalHours} h {d.Minutes} min",
    };

    private static string Plural(long n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";
}

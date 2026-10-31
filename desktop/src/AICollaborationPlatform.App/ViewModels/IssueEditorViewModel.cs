using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AICollaborationPlatform.App.ViewModels;

/// <summary>A status choice. <see cref="Value"/> null keeps the current status (same shape as <see cref="FilterOption"/>, so the drop-down style fits).</summary>
public sealed record StatusChoice(string Label, JiraTransition? Value);

/// <summary>A person in the assignee list; <see cref="Person"/> null is "Unassigned". <see cref="IsCurrent"/> marks who has it now.</summary>
public sealed record AssigneeChoice(string Name, JiraPerson? Person, bool IsCurrent)
{
    /// <summary>For the avatar: "" draws the unassigned icon.</summary>
    public string AvatarName => Person is null && !IsCurrent ? "" : Name;
}

/// <summary>
/// Editing for the issue panel: summary and description in place, status / assignee / priority in an edit mode
/// for the details box, and new comments. Nothing goes to Jira until Save or Add comment is clicked.
/// After each change the page reloads, so the table and panel show the issue as Jira now has it.
/// </summary>
public sealed partial class IssueEditorViewModel : ObservableObject
{
    public const string UnassignedName = "Unassigned";

    private readonly IJiraIssueEditor _editor;
    private readonly Func<string?> _site;
    private readonly Func<Task> _reload;
    private readonly ILogger _logger;

    private CancellationTokenSource? _choicesCts;
    private CancellationTokenSource? _searchCts;
    private IReadOnlyList<JiraPerson> _loadedAssignees = [];

    /// <param name="site">The Jira site the local data came from.</param>
    /// <param name="reload">Reloads the page from the local database after a change.</param>
    public IssueEditorViewModel(IJiraIssueEditor editor, Func<string?> site, Func<Task> reload, ILogger logger)
    {
        _editor = editor;
        _site = site;
        _reload = reload;
        _logger = logger;
    }

    /// <summary>How long to wait after typing in the people search before asking Jira. Tests set it to zero.</summary>
    internal TimeSpan SearchDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private IssueDetail? _issue;

    /// <summary>Why changes can't be made (e.g. not connected), shown in the panel; null when they can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private string? _cannotEditReason;

    public bool CanEdit => Issue is not null && CannotEditReason is null;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private FetchResultKind _messageKind;

    // ---- Summary and description ----

    [ObservableProperty]
    private bool _isEditingSummary;

    [ObservableProperty]
    private string _summaryDraft = "";

    [ObservableProperty]
    private bool _isEditingDescription;

    [ObservableProperty]
    private string _descriptionDraft = "";

    public bool DescriptionLosesFormatting => _editor.DescriptionLosesFormatting;

    // ---- Comments ----

    [ObservableProperty]
    private string _commentDraft = "";

    // ---- Details: status, assignee, priority ----

    [ObservableProperty]
    private bool _isEditingDetails;

    [ObservableProperty]
    private bool _isLoadingChoices;

    [ObservableProperty]
    private IReadOnlyList<StatusChoice> _statusChoices = [];

    [ObservableProperty]
    private StatusChoice? _selectedStatus;

    [ObservableProperty]
    private IReadOnlyList<FilterOption> _priorityChoices = [];

    [ObservableProperty]
    private FilterOption? _selectedPriority;

    [ObservableProperty]
    private IReadOnlyList<AssigneeChoice> _assigneeChoices = [];

    [ObservableProperty]
    private AssigneeChoice? _selectedAssignee;

    [ObservableProperty]
    private string _assigneeSearch = "";

    private bool DetailsChanged =>
        SelectedStatus?.Value is not null || SelectedPriority?.Value is not null || SelectedAssignee is { IsCurrent: false };

    /// <summary>True when something typed or picked would be lost by closing the panel.</summary>
    public bool HasUnsavedChanges =>
        Issue is { } issue &&
        ((IsEditingSummary && SummaryDraft.Trim() != issue.Summary) ||
         (IsEditingDescription && DescriptionDraft != (issue.Description ?? "")) ||
         !string.IsNullOrWhiteSpace(CommentDraft) ||
         (IsEditingDetails && DetailsChanged));

    /// <summary>
    /// Shows <paramref name="detail"/>. Reloading the same issue (after a save or a Refresh) keeps what's being typed;
    /// a different issue starts fresh.
    /// </summary>
    public void Load(IssueDetail? detail)
    {
        var sameIssue = detail is not null && Issue?.Id == detail.Id;
        Issue = detail;
        CannotEditReason = detail is null ? null : _editor.CannotEditReason(_site());
        if (sameIssue) return;

        _choicesCts?.Cancel();
        _searchCts?.Cancel();
        IsEditingSummary = false;
        IsEditingDescription = false;
        IsEditingDetails = false;
        CommentDraft = "";
        ShowMessage(FetchResultKind.None, "");
    }

    /// <summary>Esc while editing: cancels the open edit. Returns false if nothing was being edited.</summary>
    public bool CancelActiveEdit()
    {
        if (IsEditingDetails) CancelDetails();
        else if (IsEditingSummary) IsEditingSummary = false;
        else if (IsEditingDescription) IsEditingDescription = false;
        else return false;
        return true;
    }

    // ---- Summary ----

    [RelayCommand(CanExecute = nameof(CanStartEditing))]
    private void EditSummary()
    {
        SummaryDraft = Issue!.Summary;
        IsEditingSummary = true;
    }

    private bool CanSaveSummary() => IsEditingSummary && !IsBusy && !string.IsNullOrWhiteSpace(SummaryDraft);

    [RelayCommand(CanExecute = nameof(CanSaveSummary))]
    private Task SaveSummaryAsync()
    {
        var issue = Issue!;
        if (SummaryDraft.Trim() == issue.Summary)
        {
            IsEditingSummary = false; // nothing changed, nothing to send
            return Task.CompletedTask;
        }

        var update = new JiraIssueUpdate(Summary: SummaryDraft.Trim());
        return SaveAsync(() => _editor.UpdateTextAsync(issue.Key, update, issue.Updated), "Summary saved.", () => IsEditingSummary = false);
    }

    [RelayCommand]
    private void CancelSummary() => IsEditingSummary = false;

    // ---- Description ----

    [RelayCommand(CanExecute = nameof(CanStartEditing))]
    private void EditDescription()
    {
        DescriptionDraft = Issue!.Description ?? "";
        IsEditingDescription = true;
    }

    private bool CanSaveDescription() => IsEditingDescription && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSaveDescription))]
    private Task SaveDescriptionAsync()
    {
        var issue = Issue!;
        if (DescriptionDraft == (issue.Description ?? ""))
        {
            IsEditingDescription = false;
            return Task.CompletedTask;
        }

        var update = new JiraIssueUpdate(Description: DescriptionDraft);
        return SaveAsync(() => _editor.UpdateTextAsync(issue.Key, update, issue.Updated), "Description saved.", () => IsEditingDescription = false);
    }

    [RelayCommand]
    private void CancelDescription() => IsEditingDescription = false;

    // ---- Comments ----

    private bool CanAddComment() => CanEdit && !IsBusy && !string.IsNullOrWhiteSpace(CommentDraft);

    [RelayCommand(CanExecute = nameof(CanAddComment))]
    private Task AddCommentAsync()
    {
        var key = Issue!.Key;
        var text = CommentDraft;
        return SaveAsync(() => _editor.AddCommentAsync(key, text), "Comment added.", () => CommentDraft = "");
    }

    // ---- Details ----

    [RelayCommand(CanExecute = nameof(CanStartEditing))]
    private Task EditDetailsAsync()
    {
        IsEditingDetails = true;
        return LoadChoicesAsync();
    }

    private bool CanSaveDetails() => IsEditingDetails && !IsBusy && DetailsChanged;

    [RelayCommand(CanExecute = nameof(CanSaveDetails))]
    private Task SaveDetailsAsync()
    {
        var key = Issue!.Key;
        var assign = SelectedAssignee is { IsCurrent: false };
        var change = new IssueDetailsChange(
            SelectedPriority?.Value,
            Assign: assign,
            Assignee: assign ? SelectedAssignee!.Person : null,
            Transition: SelectedStatus?.Value);

        // On failure the editor stays open with the choices kept, so Save can simply be clicked again.
        return SaveAsync(() => _editor.UpdateDetailsAsync(key, change), "Details saved.", () => IsEditingDetails = false);
    }

    [RelayCommand]
    private void CancelDetails()
    {
        _choicesCts?.Cancel();
        _searchCts?.Cancel();
        IsEditingDetails = false;
    }

    private bool CanStartEditing() => CanEdit && !IsBusy;

    /// <summary>Fills the details editor. Each list starts with "no change", so Save sends only what was picked.</summary>
    private async Task LoadChoicesAsync()
    {
        var issue = Issue!;
        _choicesCts?.Cancel();
        var cts = _choicesCts = new CancellationTokenSource();

        StatusChoices = [new StatusChoice($"{issue.Status ?? "No status"} (current)", null)];
        SelectedStatus = StatusChoices[0];
        PriorityChoices = [new FilterOption($"{issue.Priority ?? "None"} (current)", null)];
        SelectedPriority = PriorityChoices[0];
        _loadedAssignees = [];
        SelectedAssignee = null;
        _searchCts?.Cancel();
        AssigneeSearch = "";
        ShowAssignees([]);

        IsLoadingChoices = true;
        try
        {
            var choices = await Task.Run(() => _editor.GetChoicesAsync(issue.Key, cts.Token));
            if (cts.IsCancellationRequested) return;

            StatusChoices = [StatusChoices[0], .. choices.Transitions.Select(t =>
                new StatusChoice(t.Name == t.ToStatus ? t.ToStatus : $"{t.Name} → {t.ToStatus}", t))];
            PriorityChoices = [PriorityChoices[0], .. choices.Priorities.Where(p => p != issue.Priority).Select(p => new FilterOption(p, p))];
            _loadedAssignees = choices.Assignees;
            ShowAssignees(_loadedAssignees);

            if (choices.Error is { } error) ShowMessage(FetchResultKind.Warning, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Couldn't load edit choices for {Issue}", issue.Key);
            ShowMessage(FetchResultKind.Error, $"Couldn't load the choices from Jira: {ex.Message}");
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsLoadingChoices = false;
        }
    }

    /// <summary>The latest people search, so tests can wait for it.</summary>
    internal Task AssigneeSearchTask { get; private set; } = Task.CompletedTask;

    partial void OnAssigneeSearchChanged(string value)
    {
        if (IsEditingDetails) AssigneeSearchTask = SearchAssigneesAsync(value.Trim());
    }

    // Shows matches from the people already loaded at once, then asks Jira (after a pause in typing) for anyone else.
    private async Task SearchAssigneesAsync(string query)
    {
        _searchCts?.Cancel();
        if (query.Length == 0)
        {
            ShowAssignees(_loadedAssignees);
            return;
        }

        ShowAssignees(_loadedAssignees.Where(p =>
            p.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            (p.EmailAddress?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)));

        var cts = _searchCts = new CancellationTokenSource();
        var key = Issue!.Key;
        try
        {
            if (SearchDelay > TimeSpan.Zero) await Task.Delay(SearchDelay, cts.Token);
            var found = await Task.Run(() => _editor.SearchAssigneesAsync(key, query, cts.Token));
            if (!cts.IsCancellationRequested && found.Count > 0) ShowAssignees(found);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// "Unassigned" first, then <paramref name="people"/>. Whoever has the issue now, and whoever was picked,
    /// always stay in the list, so a search never loses the selection.
    /// </summary>
    private void ShowAssignees(IEnumerable<JiraPerson> people)
    {
        if (Issue is not { } issue) return;

        var picked = SelectedAssignee;
        var choices = new List<AssigneeChoice> { new(UnassignedName, null, issue.Assignee is null) };
        choices.AddRange(people.Where(p => p.Active).DistinctBy(p => p.Id)
            .Select(p => new AssigneeChoice(p.DisplayName, p, p.DisplayName == issue.Assignee)));

        if (issue.Assignee is { } name && !choices.Any(c => c.IsCurrent))
        {
            choices.Insert(1, new AssigneeChoice(name, null, true));
        }

        // Only a real pick is carried over; "the current assignee" is found again in the new list.
        var keepPick = picked is { IsCurrent: false };
        if (keepPick && !choices.Contains(picked!))
        {
            choices.Insert(1, picked!);
        }

        AssigneeChoices = choices;
        SelectedAssignee = keepPick ? picked : choices.First(c => c.IsCurrent);
    }

    // ---- Saving ----

    private async Task SaveAsync(Func<Task<IssueEditResult>> change, string successMessage, Action onSaved)
    {
        IsBusy = true;
        ShowMessage(FetchResultKind.None, "Saving to Jira…");
        try
        {
            var result = await Task.Run(change);
            switch (result.Status)
            {
                case IssueEditStatus.Saved:
                    onSaved();
                    ShowMessage(result.Message is null ? FetchResultKind.Success : FetchResultKind.Warning, result.Message ?? successMessage);
                    break;
                case IssueEditStatus.Conflict:
                    ShowMessage(FetchResultKind.Warning, result.Message!);
                    break;
                default:
                    ShowMessage(FetchResultKind.Error, result.Message ?? "Couldn't save the change.");
                    break;
            }

            // The editor saved the issue as Jira now has it (also after a conflict or a partial save); show that.
            await _reload();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving a change to {Issue} failed unexpectedly", Issue?.Key);
            ShowMessage(FetchResultKind.Error, $"Couldn't save the change: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowMessage(FetchResultKind kind, string message)
    {
        MessageKind = kind;
        Message = message;
    }

    // ---- Keeping buttons enabled/disabled ----

    partial void OnIssueChanged(IssueDetail? value) => RefreshCommands();

    partial void OnCannotEditReasonChanged(string? value) => RefreshCommands();

    partial void OnIsBusyChanged(bool value) => RefreshCommands();

    partial void OnIsEditingSummaryChanged(bool value) => RefreshCommands();

    partial void OnSummaryDraftChanged(string value) => RefreshCommands();

    partial void OnIsEditingDescriptionChanged(bool value) => RefreshCommands();

    partial void OnCommentDraftChanged(string value) => RefreshCommands();

    partial void OnIsEditingDetailsChanged(bool value) => RefreshCommands();

    partial void OnSelectedStatusChanged(StatusChoice? value) => RefreshCommands();

    partial void OnSelectedPriorityChanged(FilterOption? value) => RefreshCommands();

    partial void OnSelectedAssigneeChanged(AssigneeChoice? value) => RefreshCommands();

    private void RefreshCommands()
    {
        EditSummaryCommand.NotifyCanExecuteChanged();
        SaveSummaryCommand.NotifyCanExecuteChanged();
        EditDescriptionCommand.NotifyCanExecuteChanged();
        SaveDescriptionCommand.NotifyCanExecuteChanged();
        AddCommentCommand.NotifyCanExecuteChanged();
        EditDetailsCommand.NotifyCanExecuteChanged();
        SaveDetailsCommand.NotifyCanExecuteChanged();
    }
}

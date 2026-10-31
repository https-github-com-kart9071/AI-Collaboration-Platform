using AICollaborationPlatform.App.Models;
using AICollaborationPlatform.App.Services;
using AICollaborationPlatform.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICollaborationPlatform.Tests;

/// <summary>Records what the editor was asked to do, and answers with <see cref="Result"/>.</summary>
internal sealed class FakeIssueEditor : IJiraIssueEditor
{
    public string? Reason { get; set; }

    public bool DescriptionLosesFormatting { get; set; }

    public IssueEditChoices Choices { get; set; } = new([], [], [], null);

    public List<JiraPerson> SearchResults { get; } = [];

    public IssueEditResult Result { get; set; } = new(IssueEditStatus.Saved);

    /// <summary>Every change sent, e.g. "comment KAN-1: Hello".</summary>
    public List<string> Sent { get; } = [];

    public DateTime? LastKnownUpdated { get; private set; }

    public IssueDetailsChange? LastDetails { get; private set; }

    public string? CannotEditReason(string? dataSite) => Reason;

    public Task<IssueEditChoices> GetChoicesAsync(string issueKey, CancellationToken cancellationToken = default) => Task.FromResult(Choices);

    public Task<IReadOnlyList<JiraPerson>> SearchAssigneesAsync(string issueKey, string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<JiraPerson>>(SearchResults);

    public Task<IssueEditResult> AddCommentAsync(string issueKey, string text, CancellationToken cancellationToken = default)
    {
        Sent.Add($"comment {issueKey}: {text}");
        return Task.FromResult(Result);
    }

    public Task<IssueEditResult> UpdateTextAsync(string issueKey, JiraIssueUpdate update, DateTime? knownUpdated, CancellationToken cancellationToken = default)
    {
        Sent.Add($"update {issueKey}: {update}");
        LastKnownUpdated = knownUpdated;
        return Task.FromResult(Result);
    }

    public Task<IssueEditResult> UpdateDetailsAsync(string issueKey, IssueDetailsChange change, CancellationToken cancellationToken = default)
    {
        Sent.Add($"details {issueKey}");
        LastDetails = change;
        return Task.FromResult(Result);
    }
}

public class IssueEditorViewModelTests
{
    private static readonly DateTime Updated = new(2026, 10, 30, 14, 0, 0);
    private static readonly JiraTransition StartProgress = new("21", "Start progress", "In Progress", "indeterminate");
    private static readonly JiraTransition Done = new("31", "Done", "Done", "done");
    private static readonly JiraPerson Sam = new("acc-sam", "Sam Lee", "sam@acme.com", true);

    private static IssueDetail Issue(string id = "1", string key = "KAN-1", string summary = "Login fails", string? assignee = "Alex Kim") =>
        new(id, key, summary, "Bug", "To Do", "new", "Medium", assignee, "Riya Shah", Updated.AddDays(-3), Updated, null,
            [], null, "Steps to reproduce", [], [], []);

    private static (IssueEditorViewModel Vm, FakeIssueEditor Editor, Func<int> Reloads) Create(IssueDetail? issue = null)
    {
        var editor = new FakeIssueEditor
        {
            Choices = new IssueEditChoices([StartProgress, Done], [TestData.Alex, TestData.Riya], ["Highest", "High", "Medium", "Low"], null),
        };
        var reloads = 0;
        var vm = new IssueEditorViewModel(editor, () => "https://acme.atlassian.net", () => { reloads++; return Task.CompletedTask; },
            NullLogger.Instance) { SearchDelay = TimeSpan.Zero };
        vm.Load(issue ?? Issue());
        return (vm, editor, () => reloads);
    }

    // ---- Summary ----

    [Fact]
    public async Task Nothing_is_sent_until_save_is_clicked()
    {
        var (vm, editor, _) = Create();

        vm.EditSummaryCommand.Execute(null);
        Assert.Equal("Login fails", vm.SummaryDraft);
        vm.SummaryDraft = "Something else";
        vm.CancelSummaryCommand.Execute(null);
        vm.CommentDraft = "Draft comment";
        await Task.Yield();

        Assert.Empty(editor.Sent);
        Assert.False(vm.IsEditingSummary);
    }

    [Fact]
    public async Task Saving_the_summary_sends_it_with_the_copy_time_then_reloads()
    {
        var (vm, editor, reloads) = Create();
        vm.EditSummaryCommand.Execute(null);
        vm.SummaryDraft = "  Login fails on Safari ";

        await vm.SaveSummaryCommand.ExecuteAsync(null);

        Assert.Equal(["update KAN-1: JiraIssueUpdate { Summary = Login fails on Safari, Description = , Priority =  }"], editor.Sent);
        Assert.Equal(Updated, editor.LastKnownUpdated); // so a newer version in Jira isn't overwritten
        Assert.False(vm.IsEditingSummary);
        Assert.Equal(1, reloads());
        Assert.Equal((FetchResultKind.Success, "Summary saved."), (vm.MessageKind, vm.Message));
    }

    [Fact]
    public async Task An_unchanged_summary_just_closes_the_editor()
    {
        var (vm, editor, reloads) = Create();
        vm.EditSummaryCommand.Execute(null);

        await vm.SaveSummaryCommand.ExecuteAsync(null);

        Assert.Empty(editor.Sent);
        Assert.Equal(0, reloads());
        Assert.False(vm.IsEditingSummary);
    }

    [Fact]
    public void An_empty_summary_cant_be_saved()
    {
        var (vm, _, _) = Create();
        vm.EditSummaryCommand.Execute(null);

        vm.SummaryDraft = "   ";

        Assert.False(vm.SaveSummaryCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_conflict_keeps_the_edit_open_so_it_can_be_saved_again()
    {
        var (vm, editor, reloads) = Create();
        editor.Result = new IssueEditResult(IssueEditStatus.Conflict, "KAN-1 was changed in Jira since this copy was fetched…");
        vm.EditSummaryCommand.Execute(null);
        vm.SummaryDraft = "My title";

        await vm.SaveSummaryCommand.ExecuteAsync(null);
        vm.Load(Issue(summary: "Renamed by Riya")); // the reload brings in Jira's newer version

        Assert.True(vm.IsEditingSummary);
        Assert.Equal("My title", vm.SummaryDraft);
        Assert.Equal("Renamed by Riya", vm.Issue!.Summary);
        Assert.Equal(FetchResultKind.Warning, vm.MessageKind);
        Assert.Equal(1, reloads());
    }

    [Fact]
    public async Task A_failure_shows_why_and_keeps_what_was_typed()
    {
        var (vm, editor, _) = Create();
        editor.Result = new IssueEditResult(IssueEditStatus.Failed, "Couldn't save: Your Jira account doesn't have permission to do this.");
        vm.EditDescriptionCommand.Execute(null);
        vm.DescriptionDraft = "New steps";

        await vm.SaveDescriptionCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditingDescription);
        Assert.Equal("New steps", vm.DescriptionDraft);
        Assert.Equal((FetchResultKind.Error, "Couldn't save: Your Jira account doesn't have permission to do this."), (vm.MessageKind, vm.Message));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Saved_with_a_warning_shows_the_warning()
    {
        var (vm, editor, _) = Create();
        editor.Result = new IssueEditResult(IssueEditStatus.Saved, "Saved in Jira, but the app's copy couldn't be updated. Use Refresh to see the change.");
        vm.CommentDraft = "Hi";

        await vm.AddCommentCommand.ExecuteAsync(null);

        Assert.Equal(FetchResultKind.Warning, vm.MessageKind);
        Assert.Equal("", vm.CommentDraft);
    }

    // ---- Comments ----

    [Fact]
    public async Task Adding_a_comment_sends_it_and_clears_the_box()
    {
        var (vm, editor, reloads) = Create();
        Assert.False(vm.AddCommentCommand.CanExecute(null)); // nothing typed yet

        vm.CommentDraft = "Fixed in build 42";
        await vm.AddCommentCommand.ExecuteAsync(null);

        Assert.Equal(["comment KAN-1: Fixed in build 42"], editor.Sent);
        Assert.Equal("", vm.CommentDraft);
        Assert.Equal("Comment added.", vm.Message);
        Assert.Equal(1, reloads());
    }

    [Fact]
    public void Editing_is_off_with_the_reason_when_changes_cant_be_made()
    {
        var editor = new FakeIssueEditor { Reason = "Connect to Jira on the Jira page to make changes." };
        var vm = new IssueEditorViewModel(editor, () => null, () => Task.CompletedTask, NullLogger.Instance);

        vm.Load(Issue());
        vm.CommentDraft = "Hello";

        Assert.False(vm.CanEdit);
        Assert.Equal("Connect to Jira on the Jira page to make changes.", vm.CannotEditReason);
        Assert.False(vm.EditSummaryCommand.CanExecute(null));
        Assert.False(vm.EditDescriptionCommand.CanExecute(null));
        Assert.False(vm.EditDetailsCommand.CanExecute(null));
        Assert.False(vm.AddCommentCommand.CanExecute(null));
    }

    // ---- Details ----

    [Fact]
    public async Task Details_editor_starts_on_the_current_values_and_lists_only_allowed_moves()
    {
        var (vm, _, _) = Create();

        await vm.EditDetailsCommand.ExecuteAsync(null);

        Assert.Equal(["To Do (current)", "Start progress → In Progress", "Done"], vm.StatusChoices.Select(c => c.Label));
        Assert.Equal(["Medium (current)", "Highest", "High", "Low"], vm.PriorityChoices.Select(c => c.Label));
        Assert.Equal(["Unassigned", "Alex Kim", "Riya Shah"], vm.AssigneeChoices.Select(c => c.Name));
        Assert.Equal("Alex Kim", vm.SelectedAssignee!.Name);
        Assert.True(vm.SelectedAssignee.IsCurrent);
        Assert.False(vm.IsLoadingChoices);
        Assert.False(vm.SaveDetailsCommand.CanExecute(null)); // nothing changed yet
    }

    [Fact]
    public async Task Saving_details_sends_only_what_was_changed()
    {
        var (vm, editor, _) = Create();
        await vm.EditDetailsCommand.ExecuteAsync(null);

        vm.SelectedStatus = vm.StatusChoices[1];
        await vm.SaveDetailsCommand.ExecuteAsync(null);

        Assert.Equal(new IssueDetailsChange(Transition: StartProgress), editor.LastDetails);
        Assert.False(vm.IsEditingDetails);
        Assert.Equal("Details saved.", vm.Message);
    }

    [Fact]
    public async Task Picking_unassigned_unassigns()
    {
        var (vm, editor, _) = Create();
        await vm.EditDetailsCommand.ExecuteAsync(null);

        vm.SelectedAssignee = vm.AssigneeChoices[0];
        vm.SelectedPriority = vm.PriorityChoices[2];
        await vm.SaveDetailsCommand.ExecuteAsync(null);

        Assert.Equal(new IssueDetailsChange(Priority: "High", Assign: true, Assignee: null), editor.LastDetails);
    }

    [Fact]
    public async Task Failed_details_stay_open_with_the_choices_kept()
    {
        var (vm, editor, _) = Create();
        editor.Result = new IssueEditResult(IssueEditStatus.Failed, "Couldn't change the status: …");
        await vm.EditDetailsCommand.ExecuteAsync(null);
        vm.SelectedStatus = vm.StatusChoices[2];

        await vm.SaveDetailsCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditingDetails);
        Assert.Equal("Done", vm.SelectedStatus!.Label);
        Assert.True(vm.SaveDetailsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Searching_people_asks_jira_and_never_loses_the_picked_person()
    {
        var (vm, editor, _) = Create();
        editor.SearchResults.Add(Sam);
        await vm.EditDetailsCommand.ExecuteAsync(null);
        vm.SelectedAssignee = vm.AssigneeChoices.Single(c => c.Name == "Riya Shah");

        vm.AssigneeSearch = "sam";
        await vm.AssigneeSearchTask;

        Assert.Equal(["Unassigned", "Riya Shah", "Alex Kim", "Sam Lee"], vm.AssigneeChoices.Select(c => c.Name));
        Assert.Equal("Riya Shah", vm.SelectedAssignee!.Name);
    }

    [Fact]
    public async Task Someone_not_in_the_list_still_shows_as_the_current_assignee()
    {
        var (vm, _, _) = Create(Issue(assignee: "Former Employee"));

        await vm.EditDetailsCommand.ExecuteAsync(null);

        Assert.Equal(new AssigneeChoice("Former Employee", null, true), vm.SelectedAssignee);
        Assert.False(vm.SaveDetailsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Choices_that_couldnt_load_are_explained()
    {
        var (vm, editor, _) = Create();
        editor.Choices = new IssueEditChoices([StartProgress], [], ["High"], "Couldn't load people: Your Jira account doesn't have permission to do this.");

        await vm.EditDetailsCommand.ExecuteAsync(null);

        Assert.Equal(FetchResultKind.Warning, vm.MessageKind);
        Assert.StartsWith("Couldn't load people", vm.Message);
        Assert.Equal(2, vm.StatusChoices.Count);
    }

    // ---- Moving between issues ----

    [Fact]
    public void Reloading_the_same_issue_keeps_what_is_being_typed_but_another_issue_starts_fresh()
    {
        var (vm, _, _) = Create();
        vm.EditDescriptionCommand.Execute(null);
        vm.DescriptionDraft = "Half-written";
        vm.CommentDraft = "Also half-written";

        vm.Load(Issue());
        Assert.True(vm.IsEditingDescription);
        Assert.Equal("Also half-written", vm.CommentDraft);

        vm.Load(Issue(id: "2", key: "KAN-2"));
        Assert.False(vm.IsEditingDescription);
        Assert.Equal("", vm.CommentDraft);
    }

    [Fact]
    public void Unsaved_changes_are_noticed()
    {
        var (vm, _, _) = Create();
        Assert.False(vm.HasUnsavedChanges);

        vm.EditSummaryCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges); // opened but not changed

        vm.SummaryDraft = "Changed";
        Assert.True(vm.HasUnsavedChanges);

        vm.CancelSummaryCommand.Execute(null);
        vm.CommentDraft = "Hi";
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public void Escape_cancels_the_open_edit_first()
    {
        var (vm, _, _) = Create();
        vm.EditSummaryCommand.Execute(null);

        Assert.True(vm.CancelActiveEdit());
        Assert.False(vm.IsEditingSummary);
        Assert.False(vm.CancelActiveEdit()); // nothing left to cancel: the panel may close
    }
}

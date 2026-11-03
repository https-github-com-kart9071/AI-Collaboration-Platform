using AICollaborationPlatform.App.Converters;

namespace AICollaborationPlatform.Tests;

public class JiraVisualsTests
{
    [Theory]
    [InlineData("Karthik Akala", "KA")]
    [InlineData("admin", "A")]
    [InlineData("  ada   lovelace  byron ", "AB")]
    [InlineData("KAN", "K")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Initials_UseFirstAndLastWord(string? name, string expected) =>
        Assert.Equal(expected, JiraVisuals.Initials(name));

    [Fact]
    public void AvatarColor_IsStablePerName_AndGreyWhenEmpty()
    {
        Assert.Same(JiraVisuals.AvatarColor("Karthik Akala"), JiraVisuals.AvatarColor("Karthik Akala"));
        Assert.Same(JiraVisuals.UnassignedColor, JiraVisuals.AvatarColor(null));
        Assert.Same(JiraVisuals.UnassignedColor, JiraVisuals.AvatarColor(""));
    }

    [Theory]
    [InlineData("Sub-task", "Subtask")]
    [InlineData("Subtask", "Subtask")]
    [InlineData("Task", "Task")]
    [InlineData("Bug", "Bug")]
    public void IssueType_SubtaskIsNotMistakenForTask(string type, string sameIconAs) =>
        Assert.Same(JiraVisuals.ForIssueType(sameIconAs), JiraVisuals.ForIssueType(type));

    [Fact]
    public void IssueType_UnknownTypesStillGetAnIcon()
    {
        Assert.NotNull(JiraVisuals.ForIssueType("Change request").Shape);
        Assert.NotNull(JiraVisuals.ForIssueType(null).Shape);
        Assert.NotSame(JiraVisuals.ForIssueType("Bug"), JiraVisuals.ForIssueType("Story"));
    }

    [Theory]
    [InlineData("Blocker", "Highest")]
    [InlineData("Critical", "Highest")]
    [InlineData("Major", "High")]
    [InlineData("Minor", "Low")]
    [InlineData("Trivial", "Lowest")]
    [InlineData("Something custom", "Medium")]
    public void Priority_ServerNamesMapToCloudLevels(string priority, string sameIconAs) =>
        Assert.Same(JiraVisuals.ForPriority(sameIconAs), JiraVisuals.ForPriority(priority));

    [Fact]
    public void Priority_NoneHasNoIcon() => Assert.Null(JiraVisuals.ForPriority(null).Shape);

    [Theory]
    [InlineData("new")]
    [InlineData(null)]
    [InlineData("something else")]
    public void Status_ToDoAndUnknownCategoriesAreGrey(string? category) =>
        Assert.Same(JiraVisuals.StatusNewBackground, JiraVisuals.ForStatus(category).Background);

    [Fact]
    public void Status_InProgressAndDoneHaveTheirOwnColours()
    {
        Assert.Equal((JiraVisuals.StatusInProgressBackground, JiraVisuals.StatusInProgressForeground), JiraVisuals.ForStatus("indeterminate"));
        Assert.Equal((JiraVisuals.StatusDoneBackground, JiraVisuals.StatusDoneForeground), JiraVisuals.ForStatus("done"));
    }

    [Fact]
    public void IssueRow_HasItsCellVisualsReady()
    {
        var row = new App.Models.IssueRow("1", "KAN-1", "KAN", "Login fails", "Bug", "In Progress", "indeterminate",
            "Karthik Akala", "High", null, null);

        Assert.Equal("IN PROGRESS", row.StatusText);
        Assert.Same(JiraVisuals.StatusInProgressForeground, row.StatusForeground);
        Assert.Same(JiraVisuals.TypeImage("Bug"), row.TypeIcon); // one shared image per icon
        Assert.NotSame(JiraVisuals.TypeImage("Story"), row.TypeIcon);
        Assert.Same(JiraVisuals.PriorityImage("High"), row.PriorityIcon);
        Assert.True(row.TypeIcon.IsFrozen);
        Assert.Equal("KA", row.AssigneeInitials);
        Assert.Same(JiraVisuals.AvatarColor("Karthik Akala"), row.AssigneeColor);
        Assert.False(row.IsUnassigned);
    }

    [Fact]
    public void IssueRow_UnassignedGetsTheGreyPersonIcon()
    {
        var row = new App.Models.IssueRow("1", "KAN-1", "KAN", "Login fails", null, null, null, null, null, null, null);

        Assert.True(row.IsUnassigned);
        Assert.Equal("", row.AssigneeInitials);
        Assert.Same(JiraVisuals.UnassignedColor, row.AssigneeColor);
        Assert.Equal("", row.StatusText);
        Assert.Null(row.PriorityIcon);
    }

    [Fact]
    public void IssueRow_EqualityIgnoresTheVisuals()
    {
        // The table keeps its selection by comparing rows, so the computed visuals mustn't affect equality.
        var a = new App.Models.IssueRow("1", "KAN-1", "KAN", "S", "Bug", "To Do", "new", "Alex", "High", null, null);
        var b = a with { };

        Assert.Equal(a, b);
    }
}

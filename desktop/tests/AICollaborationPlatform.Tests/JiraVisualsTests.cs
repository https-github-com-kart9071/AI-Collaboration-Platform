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
}

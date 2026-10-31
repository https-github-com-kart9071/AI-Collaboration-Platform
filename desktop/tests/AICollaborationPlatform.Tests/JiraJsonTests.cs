using System.Text.Json;
using AICollaborationPlatform.App.Services;

namespace AICollaborationPlatform.Tests;

public class JiraJsonTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("2024-01-15T10:30:00.000+0530", 2024, 1, 15, 10, 30, 5.5)]
    [InlineData("2024-01-15T10:30:00.000-0800", 2024, 1, 15, 10, 30, -8)]
    [InlineData("2024-01-15T10:30:00.000+0000", 2024, 1, 15, 10, 30, 0)]
    [InlineData("2024-01-15T10:30:00.000Z", 2024, 1, 15, 10, 30, 0)]
    [InlineData("2024-01-15T10:30:00+05:30", 2024, 1, 15, 10, 30, 5.5)]
    [InlineData("2024-01-15", 2024, 1, 15, 0, 0, 0)]
    public void Parses_jira_date_formats(string text, int y, int mo, int d, int h, int mi, double offsetHours)
    {
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.FromHours(offsetHours)), JiraJson.ParseDate(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void Unparseable_dates_are_null(string? text)
    {
        Assert.Null(JiraJson.ParseDate(text));
    }

    [Fact]
    public void Get_follows_paths_and_treats_missing_and_null_alike()
    {
        var json = Parse("""{"a":{"b":{"c":"x"},"n":null},"num":42,"flag":true}""");

        Assert.Equal("x", JiraJson.GetString(json, "a", "b", "c"));
        Assert.Null(JiraJson.GetString(json, "a", "missing", "c"));
        Assert.Null(JiraJson.Get(json, "a", "n"));
        Assert.Null(JiraJson.GetString(json, "a", "b", "c", "deeper"));
        Assert.Equal("42", JiraJson.GetString(json, "num"));
        Assert.Equal(42, JiraJson.GetLong(json, "num"));
        Assert.True(JiraJson.GetBool(json, "flag"));
        Assert.Null(JiraJson.GetBool(json, "num"));
    }

    [Fact]
    public void Get_long_reads_numeric_strings()
    {
        var json = Parse("""{"id":"10042","bad":"abc"}""");

        Assert.Equal(10042, JiraJson.GetLong(json, "id"));
        Assert.Null(JiraJson.GetLong(json, "bad"));
    }

    [Fact]
    public void Person_falls_back_from_account_id_to_key_to_name()
    {
        Assert.Equal("acc", JiraJson.GetPerson(Parse("""{"accountId":"acc","key":"k","name":"n"}"""))!.Id);
        Assert.Equal("k", JiraJson.GetPerson(Parse("""{"key":"k","name":"n"}"""))!.Id);
        Assert.Equal("n", JiraJson.GetPerson(Parse("""{"name":"n"}"""))!.Id);
        Assert.Null(JiraJson.GetPerson(Parse("""{"displayName":"No id"}""")));
        Assert.Null(JiraJson.GetPerson(Parse("""{"u":null}"""), "u"));
    }

    [Fact]
    public void Person_without_display_name_uses_the_id()
    {
        var person = JiraJson.GetPerson(Parse("""{"name":"sam","active":false}"""))!;

        Assert.Equal("sam", person.DisplayName);
        Assert.False(person.Active);
    }

    [Fact]
    public void Rich_text_document_becomes_readable_plain_text()
    {
        var adf = Parse("""
            {"type":"doc","version":1,"content":[
              {"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Summary"}]},
              {"type":"paragraph","content":[
                {"type":"text","text":"Ping "},{"type":"mention","attrs":{"text":"@Riya"}},
                {"type":"text","text":" "},{"type":"emoji","attrs":{"shortName":":tada:"}}]},
              {"type":"bulletList","content":[
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"First"}]}]},
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Second"}]}]}]},
              {"type":"paragraph","content":[{"type":"inlineCard","attrs":{"url":"https://example.com"}}]},
              {"type":"codeBlock","content":[{"type":"text","text":"var x = 1;"}]},
              {"type":"table","content":[{"type":"tableRow","content":[
                {"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"A"}]}]},
                {"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"B"}]}]}]}]}
            ]}
            """);

        var text = JiraJson.AdfToText(adf);

        Assert.StartsWith("Summary\nPing @Riya :tada:\n", text);
        Assert.Contains("• First", text);
        Assert.Contains("• Second", text);
        Assert.Contains("https://example.com", text);
        Assert.Contains("var x = 1;", text);
        Assert.Contains("A", text);
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Fact]
    public void Get_text_returns_server_strings_unchanged_and_null_for_missing()
    {
        var json = Parse("""{"plain":"h1. Wiki","none":null,"num":3}""");

        Assert.Equal("h1. Wiki", JiraJson.GetText(json, "plain"));
        Assert.Null(JiraJson.GetText(json, "none"));
        Assert.Null(JiraJson.GetText(json, "num"));
    }
}

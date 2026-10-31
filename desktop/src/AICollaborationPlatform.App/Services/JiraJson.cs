using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AICollaborationPlatform.App.Models;

namespace AICollaborationPlatform.App.Services;

/// <summary>
/// Helpers for reading Jira's JSON, which has optional fields everywhere
/// and differs a little between Cloud (v3) and Server (v2).
/// </summary>
public static partial class JiraJson
{
    /// <summary>Follows <paramref name="path"/> and returns the element, or null if any step is missing or JSON null.</summary>
    public static JsonElement? Get(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.Null ? null : current;
    }

    public static string? GetString(JsonElement element, params string[] path) =>
        Get(element, path) switch
        {
            { ValueKind: JsonValueKind.String } value => value.GetString(),
            { ValueKind: JsonValueKind.Number } value => value.GetRawText(), // Jira sends some IDs as numbers
            _ => null,
        };

    public static long? GetLong(JsonElement element, params string[] path) =>
        Get(element, path) switch
        {
            { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var n) => n,
            { ValueKind: JsonValueKind.String } value when long.TryParse(value.GetString(), out var n) => n,
            _ => null,
        };

    public static bool? GetBool(JsonElement element, params string[] path) =>
        Get(element, path) switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => null,
        };

    public static DateTimeOffset? GetDate(JsonElement element, params string[] path) =>
        ParseDate(GetString(element, path));

    /// <summary>
    /// Parses Jira dates. Jira writes offsets without a colon ("2024-01-15T10:30:00.000+0530"),
    /// which .NET doesn't accept, so one is added first. Agile dates use plain ISO 8601 ("…Z").
    /// </summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = OffsetWithoutColon().Replace(text.Trim(), "$1:$2");
        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
    }

    /// <summary>Maps a user object. Cloud identifies users by accountId; Server by key or name.</summary>
    public static JiraPerson? GetPerson(JsonElement element, params string[] path)
    {
        if (Get(element, path) is not { ValueKind: JsonValueKind.Object } user)
        {
            return null;
        }

        var id = GetString(user, "accountId") ?? GetString(user, "key") ?? GetString(user, "name");
        if (id is null)
        {
            return null;
        }

        return new JiraPerson(id, GetString(user, "displayName") ?? id, GetString(user, "emailAddress"), GetBool(user, "active") ?? true);
    }

    /// <summary>
    /// Returns rich text as plain text. Cloud (v3) sends Atlassian Document Format: a JSON tree;
    /// Server (v2) sends a plain string, which is returned as-is.
    /// </summary>
    public static string? GetText(JsonElement element, params string[] path) =>
        Get(element, path) switch
        {
            { ValueKind: JsonValueKind.String } value => value.GetString(),
            { ValueKind: JsonValueKind.Object } doc => AdfToText(doc),
            _ => null,
        };

    public static string AdfToText(JsonElement document)
    {
        var text = new StringBuilder();
        AppendAdf(document, text);
        return BlankLines().Replace(text.ToString(), "\n\n").Trim();
    }

    private static readonly HashSet<string> AdfBlockNodes =
        ["paragraph", "heading", "blockquote", "codeBlock", "listItem", "rule", "panel", "tableRow", "mediaSingle"];

    private static void AppendAdf(JsonElement node, StringBuilder text)
    {
        var type = GetString(node, "type");
        switch (type)
        {
            case "text":
                text.Append(GetString(node, "text"));
                return;
            case "hardBreak":
                text.Append('\n');
                return;
            case "mention":
            case "emoji":
            case "status":
            case "date":
                text.Append(GetString(node, "attrs", "text") ?? GetString(node, "attrs", "shortName") ?? "");
                return;
            case "inlineCard":
            case "blockCard":
                text.Append(GetString(node, "attrs", "url"));
                return;
            case "listItem":
                text.Append("• ");
                break;
            case "tableCell":
            case "tableHeader":
                text.Append(text.Length > 0 && text[^1] != '\n' ? " | " : "");
                break;
        }

        if (Get(node, "content") is { ValueKind: JsonValueKind.Array } content)
        {
            foreach (var child in content.EnumerateArray())
            {
                AppendAdf(child, text);
            }
        }

        if (type is not null && AdfBlockNodes.Contains(type))
        {
            text.Append('\n');
        }
    }

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex OffsetWithoutColon();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}

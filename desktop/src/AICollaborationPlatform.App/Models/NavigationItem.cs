namespace AICollaborationPlatform.App.Models;

/// <summary>
/// One entry in the sidebar. <see cref="Glyph"/> is a Segoe Fluent Icons code point.
/// </summary>
public sealed record NavigationItem(string Title, string Glyph, Type PageViewModelType, bool IsEnabled = true);

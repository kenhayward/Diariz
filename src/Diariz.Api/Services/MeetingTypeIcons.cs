namespace Diariz.Api.Services;

/// <summary>The fixed set of icon keys a meeting type may use. The web renders a matching 16x16 SVG per key;
/// the API validates that a saved <c>Icon</c> is one of these. Keep in sync with the web icon set.</summary>
public static class MeetingTypeIcons
{
    public static readonly IReadOnlyList<string> All =
    [
        "document", "handshake", "refresh", "calendar", "user", "users", "clipboard", "megaphone",
        "video", "chat", "phone", "check", "star", "briefcase", "flag", "chart",
    ];

    public static bool IsValid(string? icon) => icon is not null && All.Contains(icon);
}

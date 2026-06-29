using Diariz.Api.Contracts;

namespace Diariz.Api.Localization;

/// <summary>
/// The canonical list of languages Diariz recognises — the single source of truth for the
/// <c>GET /api/languages</c> endpoint, language-preference validation, and (later) translation targets.
/// Codes are BCP-47 tags mirroring <c>docs/potential_languages.md</c>; <see cref="LanguageDto.Rtl"/>
/// marks right-to-left scripts (Arabic, Hebrew, Persian, Urdu).
/// </summary>
public static class SupportedLanguages
{
    public static readonly IReadOnlyList<LanguageDto> All =
    [
        new("ar", "Arabic", "العربية", true),
        new("bn", "Bengali", "বাংলা", false),
        new("zh-CN", "Chinese (Simplified)", "简体中文", false),
        new("zh-TW", "Chinese (Traditional)", "繁體中文", false),
        new("cs", "Czech", "Čeština", false),
        new("da", "Danish", "Dansk", false),
        new("nl", "Dutch", "Nederlands", false),
        new("en", "English", "English", false),
        new("et", "Estonian", "Eesti", false),
        new("fi", "Finnish", "Suomi", false),
        new("fr", "French", "Français", false),
        new("de", "German", "Deutsch", false),
        new("el", "Greek", "Ελληνικά", false),
        new("gu", "Gujarati", "ગુજરાતી", false),
        new("he", "Hebrew", "עברית", true),
        new("hi", "Hindi", "हिन्दी", false),
        new("hu", "Hungarian", "Magyar", false),
        new("id", "Indonesian", "Bahasa Indonesia", false),
        new("it", "Italian", "Italiano", false),
        new("ja", "Japanese", "日本語", false),
        new("kn", "Kannada", "ಕನ್ನಡ", false),
        new("ko", "Korean", "한국어", false),
        new("lv", "Latvian", "Latviešu", false),
        new("lt", "Lithuanian", "Lietuvių", false),
        new("ms", "Malay", "Bahasa Melayu", false),
        new("ml", "Malayalam", "മലയാളം", false),
        new("mr", "Marathi", "मराठी", false),
        new("ne", "Nepali", "नेपाली", false),
        new("no", "Norwegian (Bokmål)", "Norsk bokmål", false),
        new("fa", "Persian (Farsi)", "فارسی", true),
        new("pl", "Polish", "Polski", false),
        new("pt-PT", "Portuguese (Portugal)", "Português (Portugal)", false),
        new("pt-BR", "Portuguese (Brazil)", "Português (Brasil)", false),
        new("ro", "Romanian", "Română", false),
        new("ru", "Russian", "Русский", false),
        new("sr-Cyrl", "Serbian (Cyrillic)", "Српски", false),
        new("sk", "Slovak", "Slovenčina", false),
        new("sl", "Slovenian", "Slovenščina", false),
        new("es", "Spanish", "Español", false),
        new("sv", "Swedish", "Svenska", false),
        new("ta", "Tamil", "தமிழ்", false),
        new("te", "Telugu", "తెలుగు", false),
        new("th", "Thai", "ไทย", false),
        new("tr", "Turkish", "Türkçe", false),
        new("uk", "Ukrainian", "Українська", false),
        new("ur", "Urdu", "اردو", true),
        new("vi", "Vietnamese", "Tiếng Việt", false),
    ];

    private static readonly HashSet<string> Codes = All.Select(l => l.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="code"/> is one of the supported language codes (case-insensitive).</summary>
    public static bool IsSupported(string? code) => code is not null && Codes.Contains(code);

    /// <summary>The matching language (case-insensitive), or null when unsupported.</summary>
    public static LanguageDto? Find(string? code) =>
        code is null ? null : All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
}

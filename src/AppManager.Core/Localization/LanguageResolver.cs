namespace AppManager.Core.Localization;

/// <summary>
/// Resolves the language setting ("system" or a supported UI tag) to the
/// effective UI language tag. Pure, no IO.
/// </summary>
public static class LanguageResolver
{
    public const string DefaultTag = "en-US";

    public static readonly string[] SupportedTags = { "zh-Hans", "zh-Hant", "ja-JP", "en-US" };

    /// <summary>
    /// setting: "system" or one of the supported tags (unknown → "system").
    /// preferredLanguages: the OS language list, in preference order.
    /// </summary>
    public static string Resolve(string? setting, IEnumerable<string> preferredLanguages)
    {
        if (setting is not null &&
            Array.Exists(SupportedTags, t => t.Equals(setting, StringComparison.OrdinalIgnoreCase)))
        {
            // Canonical casing.
            return Array.Find(SupportedTags,
                t => t.Equals(setting, StringComparison.OrdinalIgnoreCase))!;
        }

        foreach (var lang in preferredLanguages)
        {
            var resolved = FromTag((lang ?? string.Empty).Replace('_', '-'));
            if (resolved is not null)
                return resolved;
        }
        return DefaultTag;
    }

    private static string? FromTag(string tag)
    {
        if (tag.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase))
            return "zh-Hant";
        if (tag.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("zh-SG", StringComparison.OrdinalIgnoreCase))
            return "zh-Hans";
        if (tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return "ja-JP";
        if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return "en-US";
        return null;
    }
}

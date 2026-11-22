using System.Text;

namespace AppManager.Core.Matching;

/// <summary>
/// Normalizes app names, publisher names, folder names and exe names so they can be
/// compared: lowercase, no separators/version tokens, no corporate suffixes.
/// </summary>
public static class NameNormalizer
{
    private static readonly char[] Separators =
        { ' ', '-', '_', '.', '(', ')', '[', ']', ',', ';', '!', '®', '™', '+', '&', '/', '\\', '\'', '’', '"' };

    private static readonly HashSet<string> TrailingJunkTokens = new(StringComparer.Ordinal)
    {
        "inc", "corp", "corporation", "ltd", "llc", "gmbh", "co", "company",
        "x64", "x86", "amd64", "arm64", "32bit", "64bit", "bit",
        "version", "ver",
    };

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var s = name.Trim().ToLowerInvariant();

        // Drop " - 中文(简体)" / " - something (x64)" style trailing locale/variant suffixes.
        var dashIdx = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx >= 0)
        {
            var tail = s[(dashIdx + 3)..];
            if (tail.Contains('(') || tail.Contains('（') || LooksLikeLocaleSuffix(tail))
                s = s[..dashIdx];
        }

        s = s.TrimStart('.').Trim();

        // Drop trailing parenthesized groups: "Foo (x64)", "Foo (64-bit)", "Foo (Beta)".
        while (s.EndsWith(')') || s.EndsWith('）'))
        {
            var open = LastUnclosedParen(s);
            if (open < 0)
                break;
            s = s[..open].TrimEnd();
        }

        // Tokenize on separators and drop trailing version/junk tokens.
        var tokens = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (Array.IndexOf(Separators, c) >= 0 || char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0)
            tokens.Add(sb.ToString());

        while (tokens.Count > 0 && IsTrailingJunk(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);

        var joined = string.Concat(tokens);

        // Strip a version glued onto the end of the name: "Rider2026.2"→"rider",
        // "Arduino15"→"arduino". Keep it when nothing meaningful remains.
        var end = joined.Length;
        while (end > 0 && char.IsDigit(joined[end - 1]))
            end--;
        return end >= 3 ? joined[..end] : joined;
    }

    private static bool LooksLikeLocaleSuffix(string tail)
    {
        // e.g. "中文(简体)", "繁體中文", "français"
        foreach (var c in tail)
            if (c is >= '一' and <= '鿿')
                return true;
        return tail.Contains("简体", StringComparison.Ordinal) ||
               tail.Contains("繁體", StringComparison.Ordinal);
    }

    private static int LastUnclosedParen(string s)
    {
        var depth = 0;
        for (var i = s.Length - 1; i >= 0; i--)
        {
            if (s[i] is ')' or '）')
                depth++;
            else if (s[i] is '(' or '（')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }
        return -1;
    }

    private static bool IsTrailingJunk(string token)
    {
        if (token.Length == 0)
            return true;
        if (TrailingJunkTokens.Contains(token))
            return true;
        if (token.Length <= 2 && token.All(char.IsDigit))
            return true; // short version fragments: "12", "3"
        if (token.All(char.IsDigit))
            return true; // version numbers / years
        if (token.Length == 1)
            return true; // "s.r.o." -> s, r, o
        // version-ish like "2024a" or "v12": leading digits only handled above;
        // also drop tokens that are purely a year+minor like "2024x1"? keep simple.
        return false;
    }
}

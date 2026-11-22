using System.Text.RegularExpressions;
using System.Xml.Linq;
using AppManager.Core.Localization;
using Xunit;

namespace AppManager.Core.Tests;

public class LanguageResolverTests
{
    [Fact]
    public void System_ZhCn_ResolvesSimplified()
        => Assert.Equal("zh-Hans", LanguageResolver.Resolve("system", new[] { "zh-CN" }));

    [Fact]
    public void System_ZhTw_ResolvesTraditional()
        => Assert.Equal("zh-Hant", LanguageResolver.Resolve("system", new[] { "zh-TW" }));

    [Fact]
    public void System_ZhHk_ResolvesTraditional()
        => Assert.Equal("zh-Hant", LanguageResolver.Resolve("system", new[] { "zh-HK" }));

    [Fact]
    public void System_BareZh_ResolvesSimplified()
        => Assert.Equal("zh-Hans", LanguageResolver.Resolve("system", new[] { "zh" }));

    [Fact]
    public void System_Ja_ResolvesJapanese()
        => Assert.Equal("ja-JP", LanguageResolver.Resolve("system", new[] { "ja" }));

    [Fact]
    public void System_EnGb_ResolvesEnglish()
        => Assert.Equal("en-US", LanguageResolver.Resolve("system", new[] { "en-GB" }));

    [Fact]
    public void System_FirstSupportedPreferredWins()
        => Assert.Equal("ja-JP", LanguageResolver.Resolve("system", new[] { "fr-FR", "ja-JP" }));

    [Fact]
    public void System_NoMatch_DefaultsEnglish()
        => Assert.Equal("en-US", LanguageResolver.Resolve("system", new[] { "fr-FR" }));

    [Fact]
    public void ExplicitSetting_Wins()
        => Assert.Equal("ja-JP", LanguageResolver.Resolve("ja-JP", new[] { "zh-CN" }));

    [Fact]
    public void UnknownSetting_TreatedAsSystem()
        => Assert.Equal("zh-Hant", LanguageResolver.Resolve("xx-YY", new[] { "zh-TW" }));

    [Fact]
    public void NullSetting_TreatedAsSystem()
        => Assert.Equal("en-US", LanguageResolver.Resolve(null, new[] { "fr-FR" }));
}

/// <summary>
/// All four resource files must expose the same keys, the same {n}
/// placeholders per key, and no empty values.
/// </summary>
public class ResourceFileConsistencyTests
{
    private static readonly string[] Tags = { "zh-Hans", "zh-Hant", "ja-JP", "en-US" };

    private static string StringsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "AppManager", "Strings")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "AppManager", "Strings");
    }

    private static Dictionary<string, string> Load(string tag)
    {
        var path = Path.Combine(StringsDir(), tag, "Resources.resw");
        Assert.True(File.Exists(path), $"missing {path}");
        var doc = XDocument.Load(path);
        return doc.Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!,
                          e => e.Element("value")?.Value ?? string.Empty);
    }

    [Fact]
    public void AllFilesExist_WithIdenticalKeySets()
    {
        var baseline = Load(Tags[0]);
        Assert.True(baseline.Count > 100);
        foreach (var tag in Tags.Skip(1))
        {
            var keys = Load(tag);
            Assert.Equal(
                baseline.Keys.OrderBy(k => k),
                keys.Keys.OrderBy(k => k));
        }
    }

    [Fact]
    public void NoEmptyValues()
    {
        foreach (var tag in Tags)
            foreach (var kv in Load(tag))
                Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{tag}:{kv.Key} is empty");
    }

    private static readonly Regex Placeholder = new(@"\{\d+\}", RegexOptions.Compiled);

    [Fact]
    public void IdenticalPlaceholderSetsPerKey()
    {
        var files = Tags.ToDictionary(t => t, Load);
        foreach (var key in files[Tags[0]].Keys)
        {
            var expected = Placeholder.Matches(files[Tags[0]][key])
                .Select(m => m.Value).OrderBy(s => s).ToArray();
            foreach (var tag in Tags.Skip(1))
            {
                var actual = Placeholder.Matches(files[tag][key])
                    .Select(m => m.Value).OrderBy(s => s).ToArray();
                Assert.True(expected.SequenceEqual(actual),
                    $"{key}: {tag} placeholders [{string.Join(',', actual)}] != [{string.Join(',', expected)}]");
            }
        }
    }
}

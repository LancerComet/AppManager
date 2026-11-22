using AppManager.Core.Matching;

namespace AppManager.Core.Tests;

public class KnownToolsTests
{
    [Fact]
    public void KnownTools_Loads()
    {
        var tools = KnownTools.Load();
        Assert.NotEmpty(tools);
        Assert.True(tools.Count >= 55, $"expected >= 55 rules, got {tools.Count}");
    }

    [Fact]
    public void KnownTools_IdsAreUnique()
    {
        var tools = KnownTools.Load();
        var ids = tools.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void KnownTools_EveryRuleHasAtLeastOneFolder()
    {
        foreach (var tool in KnownTools.Load())
        {
            Assert.NotEmpty(tool.Folders);
            Assert.False(string.IsNullOrWhiteSpace(tool.DisplayName), $"rule '{tool.Id}' missing displayName");
        }
    }
}

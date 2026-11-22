using AppManager.Core.Models;
using AppManager.Core.Scanning;

namespace AppManager.Core.Tests;

public class ScanLocationProviderTests
{
    private static FolderCandidate C(string path, FolderCategory cat = FolderCategory.LocalAppData)
        => new() { Path = path, Category = cat };

    [Fact]
    public void CollapseOverlaps_RemovesNestedPaths()
    {
        var result = ScanLocationProvider.CollapseOverlaps(new[]
        {
            C("C:\\A\\B"),
            C("C:\\A"),
            C("C:\\A\\B\\C"),
            C("C:\\D"),
        });

        Assert.Equal(2, result.Count);
        Assert.Contains(result, c => c.Path == "C:\\A");
        Assert.Contains(result, c => c.Path == "C:\\D");
    }

    [Fact]
    public void CollapseOverlaps_DedupesCaseInsensitive()
    {
        var result = ScanLocationProvider.CollapseOverlaps(new[]
        {
            C("C:\\Foo"),
            C("c:\\foo"),
        });
        Assert.Single(result);
    }

    [Fact]
    public void CollapseOverlaps_SiblingPrefixNotRemoved()
    {
        var result = ScanLocationProvider.CollapseOverlaps(new[]
        {
            C("C:\\Foo"),
            C("C:\\Foobar"),
        });
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void CollapseOverlaps_TrailingSlashNormalized()
    {
        var result = ScanLocationProvider.CollapseOverlaps(new[]
        {
            C("C:\\Foo\\"),
            C("C:\\Foo\\Bar"),
        });
        Assert.Single(result);
        Assert.Equal("C:\\Foo", result[0].Path);
    }
}

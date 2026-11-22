using AppManager.Core.Matching;
using AppManager.Core.Models;

namespace AppManager.Core.Tests;

public class AppMatcherTests
{
    private static InstalledApp Reg(
        string name,
        string? publisher = null,
        string? installLocation = null,
        string? iconPath = null,
        long? estimated = null)
        => new()
        {
            Id = $"reg:HKLM64:{name}",
            Name = name,
            Publisher = publisher,
            InstallLocation = installLocation,
            IconPath = iconPath,
            EstimatedSizeBytes = estimated,
            Source = AppSource.Registry,
        };

    private static InstalledApp Msix(string name, string pfn, bool isSystem = false)
        => new()
        {
            Id = $"msix:{pfn}",
            Name = name,
            PackageFamilyName = pfn,
            Source = AppSource.Msix,
            IsSystem = isSystem,
        };

    private static FolderUsage Folder(string name, FolderCategory category, string? dir = null)
        => new()
        {
            FullPath = Path.Combine(dir ?? "C:\\Root", name),
            Name = name,
            Category = category,
            SizeBytes = 100,
            FileCount = 1,
        };

    private static KnownTool Rule(
        string id,
        string[] folders,
        FolderCategory[]? scopes = null,
        string[]? keywords = null)
        => new()
        {
            Id = id,
            DisplayName = id,
            Folders = folders.ToList(),
            Scopes = (scopes ?? Array.Empty<FolderCategory>()).ToList(),
            Keywords = (keywords ?? Array.Empty<string>()).ToList(),
        };

    private static AttributedFolder MatchOne(
        FolderUsage folder,
        IReadOnlyList<InstalledApp> apps,
        IReadOnlyList<KnownTool>? rules = null)
        => new AppMatcher(apps, rules ?? Array.Empty<KnownTool>()).Match(new[] { folder })[0];

    [Fact]
    public void PackageDataFolder_MatchesPfn_High()
    {
        var app = Msix("WhatsApp", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm");
        var result = MatchOne(
            Folder("5319275A.whatsAppDesktop_cv1g1gvanyjgm", FolderCategory.PackageData),
            new[] { app });
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Same(app, result.App);
        Assert.Equal("包数据目录 (PFN 匹配)", result.Reason);
    }

    [Fact]
    public void FolderEqualToInstallLocation_High()
    {
        var app = Reg("Foo", installLocation: "C:\\Program Files\\Foo");
        var result = MatchOne(
            Folder("Foo", FolderCategory.InstallDir, "C:\\Program Files"),
            new[] { app });
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Same(app, result.App);
    }

    [Fact]
    public void FolderContainingInstallLocation_High()
    {
        var app = Reg("Foo Bar", installLocation: "C:\\Program Files\\Foo\\Sub");
        var result = MatchOne(
            Folder("Foo", FolderCategory.InstallDir, "C:\\Program Files"),
            new[] { app });
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Same(app, result.App);
    }

    [Fact]
    public void KnownRule_WithInstalledApp_AttachesToAppHigh()
    {
        var app = Reg("Android Studio", publisher: "Google");
        var rule = Rule("android", new[] { ".android" }, keywords: new[] { "Android Studio" });
        var result = MatchOne(
            Folder(".android", FolderCategory.UserHomeDot),
            new[] { app },
            new[] { rule });
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Same(app, result.App);
        Assert.Same(rule, result.Rule);
    }

    [Fact]
    public void KnownRule_WithoutInstalledApp_ToolGroupMedium()
    {
        var rule = Rule("gradle", new[] { ".gradle" }, keywords: new[] { "Gradle" });
        var result = MatchOne(
            Folder(".gradle", FolderCategory.UserHomeDot),
            Array.Empty<InstalledApp>(),
            new[] { rule });
        Assert.Equal(MatchConfidence.Medium, result.Confidence);
        Assert.Null(result.App);
        Assert.Same(rule, result.Rule);
    }

    [Fact]
    public void KnownRule_ScopeMismatch_NoMatch()
    {
        var rule = Rule("npm", new[] { "npm" }, scopes: new[] { FolderCategory.RoamingAppData });
        var result = MatchOne(
            Folder("npm", FolderCategory.LocalAppData),
            Array.Empty<InstalledApp>(),
            new[] { rule });
        Assert.Equal(MatchConfidence.None, result.Confidence);
    }

    [Fact]
    public void PublisherFolder_SingleApp_Medium()
    {
        var app = Reg("Some App", publisher: "Acme Corp");
        var result = MatchOne(
            Folder("Acme", FolderCategory.RoamingAppData, "C:\\Roaming"),
            new[] { app });
        Assert.Equal(MatchConfidence.Medium, result.Confidence);
        Assert.Same(app, result.App);
    }

    [Fact]
    public void PublisherFolder_Ambiguous_NotMedium()
    {
        var apps = new[]
        {
            Reg("Some App", publisher: "Acme Corp"),
            Reg("Other App", publisher: "Acme Corp"),
        };
        var result = MatchOne(Folder("Acme", FolderCategory.RoamingAppData), apps);
        Assert.NotEqual(MatchConfidence.Medium, result.Confidence);
    }

    [Fact]
    public void ShortFolderName_NotHeuristicallyMatched()
    {
        var app = Reg("AB Studio", publisher: "X");
        var result = MatchOne(Folder("ab", FolderCategory.LocalAppData), new[] { app });
        Assert.Equal(MatchConfidence.None, result.Confidence);
    }

    [Fact]
    public void SystemMsixApp_NotHeuristicallyMatched()
    {
        var app = Msix("Nvidia Control Panel", "NVIDIA.ControlPanel_abc", isSystem: true);
        var result = MatchOne(
            Folder("NVIDIA Control Panel", FolderCategory.LocalAppData),
            new[] { app });
        Assert.Equal(MatchConfidence.None, result.Confidence);
    }

    [Fact]
    public void NonSystemMsixApp_HeuristicallyMatched()
    {
        var app = Msix("Spotify Music", "SpotifyAB.SpotifyMusic_abc");
        var result = MatchOne(
            Folder("Spotify Music", FolderCategory.LocalAppData),
            new[] { app });
        Assert.Equal(MatchConfidence.High, result.Confidence);
    }

    [Fact]
    public void UnmatchedFolder_None()
    {
        var result = MatchOne(
            Folder("Randomxyz", FolderCategory.LocalAppData),
            new[] { Reg("Totally Different App") });
        Assert.Equal(MatchConfidence.None, result.Confidence);
    }

    [Fact]
    public void NameEqualsInstallDirLastSegment_High()
    {
        var app = Reg("Weird Name", installLocation: "C:\\Tools\\JetBrains");
        var result = MatchOne(
            Folder("JetBrains", FolderCategory.RoamingAppData),
            new[] { app });
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Equal("名称匹配: 安装目录名", result.Reason);
    }

    [Fact]
    public void SharedInstallLocation_IsAmbiguous_NotAttributed()
    {
        // Two distinct apps reporting the same install dir: it is a container,
        // not one app's folder.
        var apps = new[]
        {
            Reg("Game One", installLocation: "D:\\Steam"),
            Reg("Game Two", installLocation: "D:\\Steam"),
        };
        var result = MatchOne(
            Folder("Steam", FolderCategory.InstallDir, "D:"),
            apps);
        Assert.Equal(MatchConfidence.None, result.Confidence);
    }

    [Fact]
    public void FolderContainingTwoAppsInstallLocations_NotAttributed()
    {
        var apps = new[]
        {
            Reg("Game One", installLocation: "D:\\Lib\\A"),
            Reg("Game Two", installLocation: "D:\\Lib\\B"),
        };
        var result = MatchOne(Folder("Lib", FolderCategory.InstallDir, "D:"), apps);
        Assert.Equal(MatchConfidence.None, result.Confidence);
    }

    [Fact]
    public void FolderContainingSingleAppInstallLocation_StillHigh()
    {
        var apps = new[]
        {
            Reg("Game One", installLocation: "D:\\Lib\\A"),
            Reg("Other", installLocation: "C:\\Elsewhere\\B"),
        };
        var result = MatchOne(Folder("Lib", FolderCategory.InstallDir, "D:"), apps);
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.Same(apps[0], result.App);
    }

    [Fact]
    public void PrefixRelation_Low()
    {
        var app = Reg("Microsoft Visual Studio Tools", publisher: "Microsoft");
        var result = MatchOne(
            Folder("Microsoft Visual", FolderCategory.RoamingAppData),
            new[] { app });
        Assert.Equal(MatchConfidence.Low, result.Confidence);
    }
}

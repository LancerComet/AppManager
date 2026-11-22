using AppManager.Core.Matching;
using AppManager.Core.Models;

namespace AppManager.Core.Tests;

public class MatchingReworkTests
{
    private static InstalledApp Reg(
        string name, string? installLocation = null, string? publisher = null, string? iconPath = null)
        => new()
        {
            Id = $"reg:HKLM64:{name}",
            Name = name,
            Publisher = publisher,
            InstallLocation = installLocation,
            IconPath = iconPath,
            Source = AppSource.Registry,
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

    private static AttributedFolder MatchOne(
        FolderUsage folder,
        IReadOnlyList<InstalledApp>? apps = null,
        IReadOnlyList<KnownTool>? rules = null)
        => new AppMatcher(apps ?? Array.Empty<InstalledApp>(), rules ?? Array.Empty<KnownTool>())
            .Match(new[] { folder })[0];

    // A1 — glued trailing version is stripped during normalization.
    [Theory]
    [InlineData("Rider2026.2", "rider")]
    [InlineData("IntelliJIdea2025.3", "intellijidea")]
    [InlineData("GoLand2024.3", "goland")]
    [InlineData("Arduino15", "arduino")]
    public void GluedTrailingVersion_IsStripped(string input, string expected)
        => Assert.Equal(expected, NameNormalizer.Normalize(input));

    [Fact]
    public void PerVersionFolder_MatchesAppByNormalizedName()
    {
        var rider = Reg("Rider", "C:\\Program Files\\JetBrains\\Rider");
        var hit = MatchOne(Folder("Rider2026.2", FolderCategory.RoamingAppData,
            "C:\\Roaming\\JetBrains"), new[] { rider });
        Assert.Same(rider, hit.App);
        Assert.Equal(MatchConfidence.High, hit.Confidence);
    }

    // A2 — generic install-dir leaf segments don't identify the app.
    [Theory]
    [InlineData("C:\\Roaming\\Microsoft", "Installer")]
    [InlineData("C:\\ProgramData\\Adobe", "Installer")]
    public void GenericInstallSegmentFolders_NotMatchedToInstallerApp(string dir, string name)
    {
        var mvsInstaller = Reg("Microsoft Visual Studio Installer",
            "C:\\Program Files (x86)\\Microsoft Visual Studio\\Installer");
        var hit = MatchOne(Folder(name, FolderCategory.RoamingAppData, dir), new[] { mvsInstaller });
        Assert.NotSame(mvsInstaller, hit.App);
    }

    // A3 — low-confidence match: prefix only, folder >= 5 chars, not generic.
    [Fact]
    public void GenericFolderName_Install_NotLowMatched()
    {
        var app = Reg("Installation Monitor");
        var hit = MatchOne(Folder("install", FolderCategory.LocalAppData), new[] { app });
        Assert.Null(hit.App);
        Assert.Equal(MatchConfidence.None, hit.Confidence);
    }

    [Fact]
    public void ShortFolderName_NotLowMatched()
    {
        var app = Reg("AbcdApp");
        var hit = MatchOne(Folder("abcd", FolderCategory.LocalAppData), new[] { app });
        Assert.Equal(MatchConfidence.None, hit.Confidence);
    }

    [Fact]
    public void PrefixMatch_StillProducesLow()
    {
        var app = Reg("Contoso Editor");
        var hit = MatchOne(Folder("contoso", FolderCategory.LocalAppData), new[] { app });
        Assert.Same(app, hit.App);
        Assert.Equal(MatchConfidence.Low, hit.Confidence);
    }

    // A4 — helper apps lose; digit tokens pick the right series version.
    [Fact]
    public void KeywordMatch_DropsHelpersAndPrefersDigitOverlap()
    {
        var installer = Reg("Microsoft Visual Studio Installer");
        var community = Reg("Visual Studio Community 2022");
        var professional = Reg("Visual Studio Professional 2019");
        var rule = new KnownTool
        {
            Id = "vsdocs",
            DisplayName = "Visual Studio",
            Folders = new() { "Visual Studio 2022" },
            Scopes = new() { FolderCategory.Documents },
            Keywords = new() { "Visual Studio" },
        };
        var hit = MatchOne(
            Folder("Visual Studio 2022", FolderCategory.Documents, "C:\\Docs"),
            new[] { installer, community, professional },
            new[] { rule });
        Assert.Same(community, hit.App);
        Assert.Equal(MatchConfidence.High, hit.Confidence);
    }

    // A5 — parent-restricted rules.
    [Fact]
    public void ParentRule_MatchesOnlyUnderNamedParent()
    {
        var rule = new KnownTool
        {
            Id = "jbshared",
            DisplayName = "JetBrains 共享数据",
            Folders = new() { "Shared" },
            Parent = "JetBrains",
        };
        var underJb = MatchOne(
            Folder("Shared", FolderCategory.RoamingAppData, "C:\\Roaming\\JetBrains"),
            rules: new[] { rule });
        Assert.Same(rule, underJb.Rule);
        Assert.Equal(MatchConfidence.Medium, underJb.Confidence);

        var elsewhere = MatchOne(
            Folder("Shared", FolderCategory.RoamingAppData, "C:\\Roaming\\Other"),
            rules: new[] { rule });
        Assert.Null(elsewhere.Rule);
        Assert.Equal(MatchConfidence.None, elsewhere.Confidence);
    }

    // Icon exe names shared by >=2 apps (launcher helpers like
    // EALaunchHelper.exe on every EA title) are ambiguous — no match.
    [Fact]
    public void SharedIconExeFolder_NotAttributed()
    {
        var icon = "D:\\Games\\ea\\EALaunchHelper.exe";
        var simCity = Reg("SimCity 4", "D:\\Steam\\steamapps\\common\\SimCity 4", iconPath: icon);
        var fifa = Reg("FIFA 23", "D:\\Steam\\steamapps\\common\\FIFA 23", iconPath: icon);
        var hit = MatchOne(Folder("EALaunchHelper", FolderCategory.LocalAppData, "C:\\Local"),
            new[] { simCity, fifa });
        Assert.Null(hit.App);
        Assert.Equal(MatchConfidence.None, hit.Confidence);
    }

    [Fact]
    public void UniqueIconExeFolder_StillMatches()
    {
        var app = Reg("MyApp", "C:\\Program Files\\MyApp",
            iconPath: "C:\\Program Files\\MyApp\\uniquehelper.exe");
        var hit = MatchOne(Folder("uniquehelper", FolderCategory.LocalAppData, "C:\\Local"), new[] { app });
        Assert.Same(app, hit.App);
        Assert.Equal(MatchConfidence.High, hit.Confidence);
    }

    // Real JSON: NuGet is a standalone tool group now (no keywords).
    [Fact]
    public void NuGetFolder_MatchesStandaloneToolGroup()
    {
        var rules = KnownTools.Load();
        var hit = MatchOne(
            Folder("NuGet", FolderCategory.LocalAppData, "C:\\Local"),
            new[] { Reg("NuGet Package Manager"), Reg("Some NuGet Tool") },
            rules);
        Assert.Null(hit.App);
        Assert.Equal("NuGet 包缓存", hit.Rule?.DisplayName);
        Assert.Equal(MatchConfidence.Medium, hit.Confidence);
    }
}

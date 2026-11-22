using AppManager.Core.Models;
using AppManager.Core.Scanning;

namespace AppManager.Core.Tests;

public class SizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(512 * 1024, "512 KB")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536 * 1024, "1.5 MB")]
    [InlineData(12L * 1024 * 1024 * 1024 + 322L * 1024 * 1024, "12.3 GB")]
    public void Format_ProducesExpected(long bytes, string expected)
    {
        Assert.Equal(expected, SizeFormatter.Format(bytes));
    }

    [Fact]
    public void Format_Terabytes()
    {
        var bytes = (long)(1.98 * 1024 * 1024 * 1024 * 1024);
        Assert.Equal("2.0 TB", SizeFormatter.Format(bytes));
        Assert.Equal("1.0 TB", SizeFormatter.Format(1024L * 1024 * 1024 * 1024));
    }
}

public class GroupBuilderTests
{
    private static InstalledApp App(string id, string? loc = null, long? est = null)
        => new()
        {
            Id = id,
            Name = id,
            InstallLocation = loc,
            EstimatedSizeBytes = est,
            Source = AppSource.Registry,
        };

    private static AttributedFolder Folder(InstalledApp app, string path, long size)
        => new()
        {
            Folder = new FolderUsage
            {
                FullPath = path,
                Name = Path.GetFileName(path),
                Category = FolderCategory.LocalAppData,
                SizeBytes = size,
                FileCount = 1,
            },
            App = app,
            Confidence = MatchConfidence.High,
            Reason = MatchReason.InstallDir,
        };

    [Fact]
    public void BytesByDrive_SplitsFoldersByDriveRoot()
    {
        var app = App("app1");
        var attributed = new[]
        {
            Folder(app, "C:\\Users\\me\\AppData\\Local\\Foo", 100),
            Folder(app, "D:\\Games\\Foo", 500),
        };
        var groups = GroupBuilder.Build(attributed, new[] { app }, "C:\\");
        Assert.Single(groups);
        Assert.Equal(600, groups[0].TotalBytes);
        Assert.Equal(new Dictionary<string, long> { ["C:\\"] = 100, ["D:\\"] = 500 },
            groups[0].BytesByDrive);
    }

    [Fact]
    public void EstimatedInstallBytes_GoToInstallLocationDrive()
    {
        var locNull = App("a1", loc: null, est: 1000);
        var locC = App("a2", loc: "C:\\Program Files\\X", est: 2000);
        var locD = App("a3", loc: "D:\\Apps\\Y", est: 4000);
        var groups = GroupBuilder.Build(
            Array.Empty<AttributedFolder>(), new[] { locNull, locC, locD }, "C:\\");
        Assert.Equal(3, groups.Count);
        // Null install location falls back to the system drive.
        Assert.Equal(1000, groups.Single(g => g.Key == "a1").BytesByDrive["C:\\"]);
        Assert.Equal(2000, groups.Single(g => g.Key == "a2").BytesByDrive["C:\\"]);
        Assert.Equal(4000, groups.Single(g => g.Key == "a3").BytesByDrive["D:\\"]);
        Assert.All(groups, g => Assert.True(g.InstallSizeIsEstimated));
    }
}

public class CsvReportBuilderTests
{
    private static readonly CsvReportBuilder.CsvLabels Labels = new()
    {
        Header = "App,Publisher,Source,TotalBytes,InstallBytes,DataBytes,Estimated,Path,Category,SizeBytes,Reason,Confidence,Drive",
        MsixSource = "Store",
        RegistrySource = "Desktop",
        ToolSource = "Tool",
        Yes = "yes",
        No = "no",
        ConfidenceName = c => c.ToString(),
        ReasonText = f => f.Reason == MatchReason.KnownRule ? $"rule:{f.ReasonArg}" : f.Reason.ToString(),
    };

    private static CsvReportBuilder.Row RowOf(AppUsageGroup g)
        => new(g, g.Folders, g.TotalBytes, g.InstallBytes, g.DataBytes);

    [Fact]
    public void Build_EscapesCommaQuoteNewline()
    {
        var group = new AppUsageGroup
        {
            Key = "k",
            DisplayName = "App, \"Quoted\"\nName",
            Publisher = "Pub,Co",
        };
        var csv = CsvReportBuilder.Build(new[] { RowOf(group) }, Labels);
        Assert.Contains("\"App, \"\"Quoted\"\"\nName\"", csv);
        Assert.Contains("\"Pub,Co\"", csv);
        Assert.StartsWith("App,", csv);
    }

    [Fact]
    public void Build_AppWithNoFolders_EmitsEmptyFolderFields()
    {
        var group = new AppUsageGroup
        {
            Key = "k",
            DisplayName = "Solo",
            InstallSizeIsEstimated = true,
            InstallBytes = 5,
            TotalBytes = 5,
        };
        var csv = CsvReportBuilder.Build(new[] { RowOf(group) }, Labels);
        Assert.EndsWith("yes,,,,,\n", csv);
    }
}

public class ScanResultStoreTests
{
    [Fact]
    public async Task SaveLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scan_{Guid.NewGuid():N}.json");
        try
        {
            var result = new ScanResult
            {
                Groups = new List<AppUsageGroup>
                {
                    new()
                    {
                        Key = "reg:HKLM64:Foo",
                        DisplayName = "Foo App",
                        Publisher = "Foo, Inc.",
                        InstallBytes = 10,
                        DataBytes = 20,
                        TotalBytes = 30,
                        BytesByDrive = new() { ["C:\\"] = 25 },
                        InstallSizeIsEstimated = true,
                        EstimatedInstallBytes = 10,
                        Folders =
                        {
                            new AttributedFolder
                            {
                                Folder = new FolderUsage
                                {
                                    FullPath = "C:\\x\\y",
                                    Name = "y",
                                    Category = FolderCategory.LocalAppData,
                                    SizeBytes = 20,
                                    FileCount = 3,
                                    LastWriteTimeUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                },
                                Confidence = MatchConfidence.High,
                                Reason = MatchReason.Publisher,
                                ReasonArg = "y",
                            },
                        },
                    },
                },
                Unattributed = new List<FolderUsage>(),
                InstalledApps = new List<InstalledApp>
                {
                    new()
                    {
                        Id = "reg:HKLM64:Foo",
                        Name = "Foo App",
                        Source = AppSource.Registry,
                        EstimatedSizeBytes = 10,
                    },
                },
                DriveTotalBytes = 100,
                DriveFreeBytes = 40,
                ScanDuration = TimeSpan.FromSeconds(2),
                StartedAtUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            };

            await ScanResultStore.SaveAsync(result, path);
            var loaded = await ScanResultStore.LoadAsync(path);

            Assert.NotNull(loaded);
            Assert.Single(loaded!.Groups);
            var g = loaded.Groups[0];
            Assert.Equal("Foo App", g.DisplayName);
            Assert.Equal(30, g.TotalBytes);
            Assert.Equal(25, g.BytesByDrive["C:\\"]);
            Assert.True(g.InstallSizeIsEstimated);
            Assert.Single(g.Folders);
            Assert.Equal(MatchReason.Publisher, g.Folders[0].Reason);
            Assert.Equal("y", g.Folders[0].ReasonArg);
            Assert.Equal(MatchConfidence.High, g.Folders[0].Confidence);
            Assert.Single(loaded.InstalledApps);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsNull()
    {
        Assert.Null(await ScanResultStore.LoadAsync(
            Path.Combine(Path.GetTempPath(), $"nope_{Guid.NewGuid():N}.json")));
    }
}

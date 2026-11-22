using AppManager.Core.Harnesses;
using AppManager.Core.Models;
using Xunit;

namespace AppManager.Core.Tests;

public class HarnessTests : IDisposable
{
    private readonly string _root;
    private readonly string _home;
    private readonly string _roaming;
    private readonly string _local;

    public HarnessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harness-tests-" + Guid.NewGuid().ToString("N"));
        _home = Path.Combine(_root, "home");
        _roaming = Path.Combine(_root, "roaming");
        _local = Path.Combine(_root, "local");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_roaming);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        try
        {
            // junctions must be removed without traversal
            foreach (var j in Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(j) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(j);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private IReadOnlyList<string> Resolve(string token) => token.ToUpperInvariant() switch
    {
        "USERPROFILE" => [_home],
        "APPDATA" => [_roaming],
        "LOCALAPPDATA" => [_local],
        _ => throw new ArgumentException(token),
    };

    private string Write(string baseDir, string rel, int bytes = 10, DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(baseDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (lastWriteUtc is { } lw)
            File.SetLastWriteTimeUtc(path, lw);
        return path;
    }

    private HarnessScanResult Scan(string json)
    {
        var catalog = HarnessCatalog.LoadFromJson(json);
        var scanner = new HarnessScanner(catalog, Resolve);
        return scanner.ScanAsync(Array.Empty<InstalledApp>(), CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static string Catalog(params object[] harnesses)
        => "{ \"presets\": {}, \"harnesses\": [" + string.Join(",", harnesses) + "] }";

    // ---------- catalog load / validation ----------

    [Fact]
    public void RealCatalog_Loads_AndIsValid()
    {
        var catalog = HarnessCatalog.Load();
        Assert.NotEmpty(catalog.Harnesses);
        Assert.Equal(catalog.Harnesses.Count,
            catalog.Harnesses.Select(h => h.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(catalog.Harnesses, h => Assert.NotEmpty(h.Name));
    }

    [Fact]
    public void Catalog_DuplicateId_Throws()
        => Assert.Throws<InvalidDataException>(() => HarnessCatalog.LoadFromJson(
            Catalog(
                "{\"id\":\"a\",\"name\":\"A\",\"type\":\"cli\",\"roots\":[]}",
                "{\"id\":\"A\",\"name\":\"B\",\"type\":\"cli\",\"roots\":[]}")));

    [Fact]
    public void Catalog_UnknownKind_Throws()
        => Assert.Throws<InvalidDataException>(() => HarnessCatalog.LoadFromJson(
            Catalog("{\"id\":\"a\",\"name\":\"A\",\"type\":\"cli\",\"roots\":[" +
                    "{\"path\":\"%USERPROFILE%\\\\x\",\"kind\":\"nonsense\"}]}")));

    [Fact]
    public void Catalog_UnknownPreset_Throws()
        => Assert.Throws<InvalidDataException>(() => HarnessCatalog.LoadFromJson(
            "{\"presets\":{},\"harnesses\":[{\"id\":\"a\",\"name\":\"A\",\"type\":\"cli\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\x\",\"kind\":\"state\",\"children\":[{\"preset\":\"nope\"}]}]}]}"));

    [Fact]
    public void Catalog_PresetCycle_Throws()
        => Assert.Throws<InvalidDataException>(() => HarnessCatalog.LoadFromJson(
            "{\"presets\":{\"p\":[{\"preset\":\"q\"}],\"q\":[{\"preset\":\"p\"}]}," +
            "\"harnesses\":[{\"id\":\"a\",\"name\":\"A\",\"type\":\"cli\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\x\",\"kind\":\"state\",\"children\":[{\"preset\":\"p\"}]}]}]}"));

    [Fact]
    public void Catalog_BadToken_Throws()
        => Assert.Throws<InvalidDataException>(() => HarnessCatalog.LoadFromJson(
            Catalog("{\"id\":\"a\",\"name\":\"A\",\"type\":\"cli\",\"roots\":[" +
                    "{\"path\":\"%WINDIR%\\\\x\",\"kind\":\"state\"}]}")));

    [Fact]
    public void Catalog_WildcardMidPath_Throws()
        => Assert.Throws<InvalidDataException>(() => HarnessCatalog.LoadFromJson(
            Catalog("{\"id\":\"a\",\"name\":\"A\",\"type\":\"cli\",\"roots\":[" +
                    "{\"path\":\"%USERPROFILE%\\\\foo*\\\\bar\",\"kind\":\"state\"}]}")));

    [Fact]
    public void Catalog_DuplicateRootPath_Throws()
        => Assert.Throws<InvalidDataException>(() => HarnessCatalog.LoadFromJson(
            Catalog("{\"id\":\"a\",\"name\":\"A\",\"type\":\"cli\",\"roots\":[" +
                    "{\"path\":\"%USERPROFILE%\\\\x\",\"kind\":\"state\"}," +
                    "{\"path\":\"%USERPROFILE%\\\\x\",\"kind\":\"cache\"}]}")));

    // ---------- classification ----------

    [Fact]
    public async Task RulePrecedence_ExactBeforeGlobBeforeRootDefault()
    {
        Write(_home, "h\\precise.txt", 10);
        Write(_home, "h\\anything.tmp", 20);
        Write(_home, "h\\rest.dat", 40);
        var result = Scan(
            "{\"presets\":{\"p\":[{\"match\":\"*.tmp\",\"kind\":\"logs\"}]}," +
            "\"harnesses\":[{\"id\":\"h\",\"name\":\"H\",\"type\":\"cli\",\"roots\":[{" +
            "\"path\":\"%USERPROFILE%\\\\h\",\"kind\":\"state\",\"children\":[" +
            "{\"preset\":\"p\"}," +
            "{\"match\":\"precise.txt\",\"kind\":\"artifacts\"}]}]}]}");

        var h = Assert.Single(result.Harnesses);
        Assert.Equal(10, h.BytesByKind[HarnessKind.Artifacts]);
        Assert.Equal(20, h.BytesByKind[HarnessKind.Logs]);
        Assert.Equal(40, h.BytesByKind[HarnessKind.State]);
        Assert.Equal(70, h.TotalBytes);
    }

    [Fact]
    public async Task FileRoot_And_GlobRoot()
    {
        Write(_home, "conf.json", 11);
        Write(_home, "conf.json.backup", 22);
        Directory.CreateDirectory(Path.Combine(_home, "otherdir")); // not matched by glob
        var result = Scan(Catalog(
            "{\"id\":\"h\",\"name\":\"H\",\"type\":\"cli\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\conf.json*\",\"kind\":\"state\"}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Equal(2, h.Entries.Count);
        Assert.All(h.Entries, e => Assert.True(e.IsFile));
        Assert.Equal(33, h.TotalBytes);
    }

    [Fact]
    public async Task MissingRoot_Ignored_AndAbsentHarness()
    {
        Write(_home, "exists\\a.txt", 5);
        var result = Scan(Catalog(
            "{\"id\":\"h\",\"name\":\"H\",\"type\":\"cli\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\exists\",\"kind\":\"state\"}," +
            "{\"path\":\"%USERPROFILE%\\\\nothere\",\"kind\":\"state\"}]}",
            "{\"id\":\"gone\",\"name\":\"Gone\",\"type\":\"cli\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\absent\",\"kind\":\"state\"}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Equal("h", h.Id);
        Assert.Equal(5, h.TotalBytes);
    }

    [Fact]
    public async Task NestedClaims_ExcludedAtChildAndDeeper()
    {
        // A's root contains: sub (claimed by B) skipped; deep containing claimed
        // (A's own nested root) excluded; free dir counted.
        Write(_home, "A\\sub\\b.txt", 60);
        Write(_home, "A\\deep\\claimed\\c.txt", 30);
        Write(_home, "A\\deep\\file.txt", 40);
        Write(_home, "A\\free\\f.txt", 20);

        var result = Scan(
            "{\"presets\":{},\"harnesses\":[" +
            "{\"id\":\"a\",\"name\":\"A\",\"type\":\"cli\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\A\",\"kind\":\"state\"}," +
            "{\"path\":\"%USERPROFILE%\\\\A\\\\deep\\\\claimed\",\"kind\":\"cache\"}]}," +
            "{\"id\":\"b\",\"name\":\"B\",\"type\":\"cli\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\A\\\\sub\",\"kind\":\"state\"}]}]}");

        var a = result.Harnesses.Single(h => h.Id == "a");
        var b = result.Harnesses.Single(h => h.Id == "b");

        // A: free(20) + deep-excluding-claimed(40) + claimed-root child c.txt(30) = 90
        Assert.Equal(90, a.TotalBytes);
        var deep = a.Entries.Single(e => e.Name == "deep");
        Assert.Equal(40, deep.SizeBytes);
        var claimedChild = a.Entries.Single(e => e.Name == "c.txt");
        Assert.Equal(30, claimedChild.SizeBytes);
        Assert.Equal(HarnessKind.Cache, claimedChild.Kind);
        // sub must not appear under A
        Assert.DoesNotContain(a.Entries, e => e.Name == "sub");
        // B sees sub with its content
        Assert.Equal(60, b.TotalBytes);
    }

    [Fact]
    public async Task ProgramChild_ExcludedFromTotals()
    {
        Write(_home, "h\\prog\\app.exe", 50);
        Write(_home, "h\\data\\d.txt", 30);
        var result = Scan(Catalog(
            "{\"id\":\"h\",\"name\":\"H\",\"type\":\"cli\",\"roots\":[{" +
            "\"path\":\"%USERPROFILE%\\\\h\",\"kind\":\"state\",\"children\":[" +
            "{\"match\":\"prog\",\"kind\":\"program\"}]}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Equal(30, h.TotalBytes);
        Assert.Equal(50, h.ExcludedProgramBytes);
        Assert.Single(h.Entries);
    }

    [Fact]
    public async Task VersionsRoot_NewestNonEmpty_IsProgram()
    {
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newest = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Write(_home, "V\\v1\\app.exe", 10, old);
        Write(_home, "V\\v2\\app.exe", 20, newest);
        Directory.CreateDirectory(Path.Combine(_home, "V", "emptyver"));
        Write(_home, "V\\stray.bin", 5, old); // file member of the version set

        var result = Scan(Catalog(
            "{\"id\":\"h\",\"name\":\"H\",\"type\":\"cli\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\V\",\"kind\":\"versions\"}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Equal(20, h.ExcludedProgramBytes); // v2 current
        Assert.Equal(2, h.Entries.Count);         // v1 + stray.bin as oldVersions
        Assert.All(h.Entries, e => Assert.Equal(HarnessKind.OldVersions, e.Kind));
        Assert.Equal(15, h.TotalBytes);
        Assert.DoesNotContain(h.Entries, e => e.Name == "emptyver");
    }

    [Fact]
    public async Task VersionsChild_AppliesSameSemantics()
    {
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newest = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Write(_home, "h\\vers\\1\\f", 10, old);
        Write(_home, "h\\vers\\2\\f", 20, newest);
        Write(_home, "h\\data\\d", 7);
        var result = Scan(Catalog(
            "{\"id\":\"h\",\"name\":\"H\",\"type\":\"cli\",\"roots\":[{" +
            "\"path\":\"%USERPROFILE%\\\\h\",\"kind\":\"state\",\"children\":[" +
            "{\"match\":\"vers\",\"kind\":\"versions\"}]}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Equal(20, h.ExcludedProgramBytes);
        var old1 = Assert.Single(h.Entries, e => e.Name == "1");
        Assert.Equal(HarnessKind.OldVersions, old1.Kind);
        Assert.Equal(17, h.TotalBytes); // 10 (old version) + 7 (data)
    }

    [Fact]
    public async Task VersionSet_OnFiles()
    {
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newest = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Write(_home, "h\\runner-1.exe", 10, old);
        Write(_home, "h\\runner-2.exe", 20, newest);
        Write(_home, "h\\other.txt", 5);
        var result = Scan(Catalog(
            "{\"id\":\"h\",\"name\":\"H\",\"type\":\"cli\",\"roots\":[{" +
            "\"path\":\"%USERPROFILE%\\\\h\",\"kind\":\"state\",\"children\":[" +
            "{\"match\":\"runner-*.exe\",\"kind\":\"versionSet\"}]}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Equal(20, h.ExcludedProgramBytes); // runner-2 is current
        var old1 = Assert.Single(h.Entries, e => e.Name == "runner-1.exe");
        Assert.Equal(HarnessKind.OldVersions, old1.Kind);
        Assert.Equal(15, h.TotalBytes); // runner-1 (10) + other (5)
    }

    [Fact]
    public async Task ProgramRoot_WithChildOverrides()
    {
        Write(_home, "h\\app.exe", 40);
        Write(_home, "h\\app.exe.old", 7);
        var result = Scan(Catalog(
            "{\"id\":\"h\",\"name\":\"H\",\"type\":\"cli\",\"roots\":[{" +
            "\"path\":\"%USERPROFILE%\\\\h\",\"kind\":\"program\",\"children\":[" +
            "{\"match\":\"*.old\",\"kind\":\"oldVersions\"}]}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Equal(40, h.ExcludedProgramBytes);
        var e = Assert.Single(h.Entries);
        Assert.Equal(HarnessKind.OldVersions, e.Kind);
        Assert.Equal(7, h.TotalBytes);
    }

    [Fact]
    public async Task JunctionChild_Skipped()
    {
        var target = Path.Combine(_root, "junction-target");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "t.txt"), new byte[99]);
        Write(_home, "h\\real.txt", 10);
        Directory.CreateDirectory(Path.Combine(_home, "h"));
        var link = Path.Combine(_home, "h", "linked");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        System.Diagnostics.Process.Start(psi)!.WaitForExit();
        Assert.True(Directory.Exists(link));

        var result = Scan(Catalog(
            "{\"id\":\"h\",\"name\":\"H\",\"type\":\"cli\",\"roots\":[{" +
            "\"path\":\"%USERPROFILE%\\\\h\",\"kind\":\"state\"}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Single(h.Entries);
        Assert.Equal(10, h.TotalBytes); // junction's 99 bytes excluded entirely
    }

    [Fact]
    public async Task ZeroByteHarness_IsSkipped()
    {
        Write(_home, "h\\empty.txt", 0);
        Write(_home, "real\\f.txt", 10);
        var result = Scan(
            "{\"presets\":{},\"harnesses\":[" +
            "{\"id\":\"zero\",\"name\":\"Z\",\"type\":\"cli\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\h\",\"kind\":\"state\"}]}," +
            "{\"id\":\"real\",\"name\":\"R\",\"type\":\"cli\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\real\",\"kind\":\"state\"}]}]}");

        var h = Assert.Single(result.Harnesses);
        Assert.Equal("real", h.Id);
    }

    [Fact]
    public async Task ZeroTotal_WithProgramBytes_IsKept()
    {
        Write(_home, "h\\prog\\app.exe", 50);
        var result = Scan(Catalog(
            "{\"id\":\"h\",\"name\":\"H\",\"type\":\"cli\",\"roots\":[{" +
            "\"path\":\"%USERPROFILE%\\\\h\",\"kind\":\"state\",\"children\":[" +
            "{\"match\":\"prog\",\"kind\":\"program\"}]}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Equal(0, h.TotalBytes);
        Assert.Equal(50, h.ExcludedProgramBytes);
    }

    [Fact]
    public async Task VersionsOnFileRoot_IsCatalogError()
    {
        Write(_home, "f.txt", 1);
        var catalog = HarnessCatalog.LoadFromJson(Catalog(
            "{\"id\":\"h\",\"name\":\"H\",\"type\":\"cli\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\f.txt\",\"kind\":\"versions\"}]}"));
        var scanner = new HarnessScanner(catalog, Resolve);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => scanner.ScanAsync(Array.Empty<InstalledApp>(), CancellationToken.None));
    }
}

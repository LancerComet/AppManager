using AppManager.Core.Harnesses;
using AppManager.Core.Models;
using Xunit;

namespace AppManager.Core.Tests;

/// <summary>
/// DevTools catalog: Machine-scope tokens, sources (env/ini/xml), merge mode,
/// labels/prefixes and the source-path safety guard. All paths live under a
/// per-test temp dir via the injected resolver and env lookup.
/// </summary>
public class DevToolsCatalogTests : IDisposable
{
    private readonly string _root;
    private readonly string _home;
    private readonly string _roaming;
    private readonly string _local;
    private readonly string _pf;
    private readonly string _pfx86;
    private readonly string _pdata;
    private readonly string _driveA;
    private readonly string _driveB;
    private readonly Dictionary<string, string?> _envVars = new(StringComparer.OrdinalIgnoreCase);

    public DevToolsCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "devtools-tests-" + Guid.NewGuid().ToString("N"));
        _home = Path.Combine(_root, "home");
        _roaming = Path.Combine(_root, "roaming");
        _local = Path.Combine(_root, "local");
        _pf = Path.Combine(_root, "pf");
        _pfx86 = Path.Combine(_root, "pfx86");
        _pdata = Path.Combine(_root, "pdata");
        _driveA = Path.Combine(_root, "driveA");
        _driveB = Path.Combine(_root, "driveB");
        foreach (var d in new[] { _home, _roaming, _local, _pf, _pfx86, _pdata, _driveA, _driveB })
            Directory.CreateDirectory(d);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private IReadOnlyList<string> Resolve(string token) => token.ToUpperInvariant() switch
    {
        "USERPROFILE" => [_home],
        "APPDATA" => [_roaming],
        "LOCALAPPDATA" => [_local],
        "PROGRAMFILES" => [_pf],
        "PROGRAMFILES(X86)" => [_pfx86],
        "PROGRAMDATA" => [_pdata],
        "DRIVES" => [_driveA, _driveB],
        _ => throw new ArgumentException(token),
    };

    private string? Env(string name) => _envVars.TryGetValue(name, out var v) ? v : null;

    private string Write(string baseDir, string rel, int bytes = 10)
    {
        var path = Path.Combine(baseDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private HarnessScanResult Scan(string json)
    {
        var catalog = HarnessCatalog.LoadFromJson(json, HarnessCatalogScope.Machine);
        var scanner = new HarnessScanner(catalog, Resolve, Env);
        return scanner.ScanAsync(Array.Empty<InstalledApp>(), CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static string Catalog(params object[] harnesses)
        => "{ \"presets\": {}, \"harnesses\": [" + string.Join(",", harnesses) + "] }";

    // ---------- catalog loading ----------

    [Fact]
    public void DevCatalog_Loads_WithMachineScope()
    {
        var catalog = HarnessCatalog.LoadDevTools();
        Assert.NotEmpty(catalog.Harnesses);
        Assert.Equal(catalog.Harnesses.Count,
            catalog.Harnesses.Select(h => h.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(catalog.Harnesses, h => Assert.NotEmpty(h.Name));
    }

    [Fact]
    public void AiCatalog_StillLoads_CurrentUser()
    {
        var catalog = HarnessCatalog.Load();
        Assert.NotEmpty(catalog.Harnesses);
    }

    [Fact]
    public void ProgramFiles_Rejected_UnderCurrentUser()
        => Assert.Throws<InvalidDataException>(() => HarnessCatalog.LoadFromJson(
            Catalog("{\"id\":\"a\",\"name\":\"A\",\"type\":\"runtime\",\"roots\":[" +
                    "{\"path\":\"%PROGRAMFILES%\\\\x\",\"kind\":\"sdk\"}]}")));

    [Fact]
    public void MachineScope_AcceptsMachineTokens()
    {
        var catalog = HarnessCatalog.LoadFromJson(Catalog(
            "{\"id\":\"a\",\"name\":\"A\",\"type\":\"runtime\",\"roots\":[" +
            "{\"path\":\"%PROGRAMFILES%\\\\x\",\"kind\":\"sdk\"}," +
            "{\"path\":\"%PROGRAMFILES(X86)%\\\\y\",\"kind\":\"sdk\"}," +
            "{\"path\":\"%PROGRAMDATA%\\\\z\",\"kind\":\"sdk\"}," +
            "{\"path\":\"%DRIVES%\\\\w\",\"kind\":\"sdk\"}]}"),
            HarnessCatalogScope.Machine);
        Assert.Equal(4, catalog.Harnesses[0].Roots.Count);
    }

    // ---------- validation ----------

    [Fact]
    public void Root_WithoutPathOrSources_Throws()
        => Assert.Throws<InvalidDataException>(() => HarnessCatalog.LoadFromJson(
            Catalog("{\"id\":\"a\",\"name\":\"A\",\"type\":\"runtime\",\"roots\":[" +
                    "{\"kind\":\"sdk\"}]}"), HarnessCatalogScope.Machine));

    [Fact]
    public void MergeRoot_WithVersionsKind_Throws()
        => Assert.Throws<InvalidDataException>(() => HarnessCatalog.LoadFromJson(
            Catalog("{\"id\":\"a\",\"name\":\"A\",\"type\":\"runtime\",\"roots\":[" +
                    "{\"path\":\"%USERPROFILE%\\\\x\",\"kind\":\"versions\",\"mode\":\"merge\"}]}")));

    [Fact]
    public void MergeRoot_WithVersionsChildRule_Throws()
        => Assert.Throws<InvalidDataException>(() => HarnessCatalog.LoadFromJson(
            Catalog("{\"id\":\"a\",\"name\":\"A\",\"type\":\"runtime\",\"roots\":[" +
                    "{\"path\":\"%USERPROFILE%\\\\x\",\"kind\":\"sdk\",\"mode\":\"merge\"," +
                    "\"children\":[{\"match\":\"v*\",\"kind\":\"versions\"}]}]}")));

    [Fact]
    public void UnknownSourceType_Throws()
        => Assert.Throws<InvalidDataException>(() => HarnessCatalog.LoadFromJson(
            Catalog("{\"id\":\"a\",\"name\":\"A\",\"type\":\"runtime\",\"roots\":[" +
                    "{\"kind\":\"sdk\",\"sources\":[{\"registry\":\"x\"}]}]}"),
            HarnessCatalogScope.Machine));

    // ---------- merge / prefix ----------

    [Fact]
    public async Task Merge_SplitsMatchedChildren_AndMergesRest()
    {
        Write(_home, "R\\a\\pa.txt", 10);
        Write(_home, "R\\b\\pb.txt", 20);
        Write(_home, "R\\f.txt", 5);
        var result = Scan(Catalog(
            "{\"id\":\"r\",\"name\":\"R\",\"type\":\"runtime\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\R\",\"kind\":\"sdk\",\"mode\":\"merge\",\"label\":\"R merged\"," +
            "\"children\":[{\"match\":\"a\",\"kind\":\"packageCache\"}]}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Equal(2, h.Entries.Count);
        var a = Assert.Single(h.Entries, e => e.Name == "a");
        Assert.Equal(HarnessKind.PackageCache, a.Kind);
        Assert.Equal(10, a.SizeBytes);
        var merged = Assert.Single(h.Entries, e => e.Name == "R merged");
        Assert.Equal(HarnessKind.Sdk, merged.Kind);
        Assert.Equal(25, merged.SizeBytes); // b + f.txt
        Assert.Equal(35, h.TotalBytes);
    }

    [Fact]
    public async Task Merge_WithoutRules_IsSingleResidualEntry()
    {
        Write(_home, "R\\a\\x.txt", 10);
        Write(_home, "R\\top.txt", 5);
        var result = Scan(Catalog(
            "{\"id\":\"r\",\"name\":\"R\",\"type\":\"runtime\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\R\",\"kind\":\"sdk\",\"mode\":\"merge\"}]}"));

        var h = Assert.Single(result.Harnesses);
        var e = Assert.Single(h.Entries);
        Assert.Equal("R", e.Name);
        Assert.Equal(15, e.SizeBytes);
    }

    [Fact]
    public async Task Split_EntryPrefix_NamesChildren()
    {
        Write(_home, "S\\1.0\\f.txt", 10);
        Write(_home, "S\\2.0\\f.txt", 20);
        var result = Scan(Catalog(
            "{\"id\":\"s\",\"name\":\"S\",\"type\":\"runtime\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\S\",\"kind\":\"sdk\",\"entryPrefix\":\"Prefix\"}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Equal(2, h.Entries.Count);
        Assert.Contains(h.Entries, e => e.Name == "Prefix 1.0" && e.SizeBytes == 10);
        Assert.Contains(h.Entries, e => e.Name == "Prefix 2.0" && e.SizeBytes == 20);
    }

    // ---------- env source ----------

    [Fact]
    public async Task EnvSource_MultiValue_WithSuffix()
    {
        var x = Path.Combine(_root, "envx");
        var y = Path.Combine(_root, "envy");
        Write(x, "pkgs\\a.txt", 10);
        Write(y, "pkgs\\b.txt", 20);
        _envVars["MY_PACKAGES"] = $"{x};{y}";

        var result = Scan(Catalog(
            "{\"id\":\"p\",\"name\":\"P\",\"type\":\"packageManager\",\"roots\":[" +
            "{\"kind\":\"packageCache\",\"sources\":[{\"env\":\"MY_PACKAGES\",\"suffix\":\"pkgs\"}]}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Equal(2, h.Entries.Count);
        Assert.Equal(30, h.TotalBytes);
    }

    [Fact]
    public async Task EnvSource_SameAsDefaultPath_MeasuredOnce()
    {
        var dir = Path.Combine(_home, "dup");
        Write(dir, "f.txt", 10);
        _envVars["DUP_DIR"] = $"\"{dir}\""; // quoted

        var result = Scan(Catalog(
            "{\"id\":\"d\",\"name\":\"D\",\"type\":\"packageManager\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\dup\",\"kind\":\"packageCache\",\"mode\":\"merge\"," +
            "\"sources\":[{\"env\":\"DUP_DIR\"}]}]}"));

        var h = Assert.Single(result.Harnesses);
        var e = Assert.Single(h.Entries);
        Assert.Equal(10, e.SizeBytes);
    }

    // ---------- ini / xml sources ----------

    [Fact]
    public async Task IniSource_KeyValue_CommentsQuotesAndExpansion()
    {
        var target = Path.Combine(_root, "inivalue");
        Write(target, "f.txt", 10);
        _envVars["INI_BASE"] = _root;
        File.WriteAllLines(Path.Combine(_home, ".myrc"), new[]
        {
            "; a comment line",
            "# another comment",
            "//registry.npmjs.org/:_authToken=SECRET",
            "OTHER_KEY=nope",
            "cache= \"${INI_BASE}/inivalue\"",
            "CACHE=ignored-second-match",
        });

        var result = Scan(Catalog(
            "{\"id\":\"n\",\"name\":\"N\",\"type\":\"packageManager\",\"roots\":[" +
            "{\"kind\":\"packageCache\",\"sources\":[" +
            "{\"ini\":\"%USERPROFILE%\\\\.myrc\",\"key\":\"cache\"}]}]}"));

        var h = Assert.Single(result.Harnesses);
        var e = Assert.Single(h.Entries);
        Assert.Equal(10, e.SizeBytes);
        Assert.DoesNotContain("SECRET", e.Path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", e.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", result.Harnesses.SelectMany(x => x.Entries)
            .Select(x => x.Path + x.Name).Aggregate((a, b) => a + b), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task XmlSource_ResolvesLocalRepository()
    {
        var repo = Path.Combine(_root, "m2repo");
        Write(repo, "f.txt", 10);
        File.WriteAllText(Path.Combine(_home, "settings.xml"),
            "<settings><localRepository>" + repo + "</localRepository></settings>");

        var result = Scan(Catalog(
            "{\"id\":\"m\",\"name\":\"M\",\"type\":\"buildTool\",\"roots\":[" +
            "{\"kind\":\"packageCache\",\"mode\":\"merge\",\"sources\":[" +
            "{\"xml\":\"%USERPROFILE%\\\\settings.xml\",\"element\":\"localRepository\"}]}]}"));

        var h = Assert.Single(result.Harnesses);
        var e = Assert.Single(h.Entries);
        Assert.Equal(repo, e.Path);
        Assert.Equal(10, e.SizeBytes);
    }

    // ---------- safety guard ----------

    [Fact]
    public async Task SafetyGuard_DropsDangerousSourcePaths()
    {
        var valid = Path.Combine(_root, "validcache");
        Write(valid, "f.txt", 10);
        var driveRoot = Path.GetPathRoot(_root)!;          // e.g. C:\
        _envVars["BAD_ROOT"] = driveRoot;
        _envVars["BAD_HOME"] = _home;                       // equals user-profile base
        _envVars["BAD_PARENT"] = _root;                     // ancestor of the base
        _envVars["GOOD"] = valid;

        var result = Scan(Catalog(
            "{\"id\":\"g\",\"name\":\"G\",\"type\":\"tooling\",\"roots\":[" +
            "{\"kind\":\"cache\",\"mode\":\"merge\",\"sources\":[" +
            "{\"env\":\"BAD_ROOT\"},{\"env\":\"BAD_HOME\"},{\"env\":\"BAD_PARENT\"}," +
            "{\"env\":\"GOOD\"}]}]}"));

        var h = Assert.Single(result.Harnesses);
        var e = Assert.Single(h.Entries);
        Assert.Equal(valid, e.Path);
        Assert.Equal(10, e.SizeBytes);
    }

    // ---------- DRIVES ----------

    [Fact]
    public async Task DrivesToken_ExpandsToEveryResolvedBase()
    {
        Write(_driveA, ".store\\a.txt", 10);
        Write(_driveB, ".store\\b.txt", 20);

        var result = Scan(Catalog(
            "{\"id\":\"s\",\"name\":\"S\",\"type\":\"packageManager\",\"roots\":[" +
            "{\"path\":\"%DRIVES%\\\\.store\",\"kind\":\"packageCache\",\"mode\":\"merge\"}]}"));

        var h = Assert.Single(result.Harnesses);
        Assert.Equal(2, h.Entries.Count);
        Assert.Equal(30, h.TotalBytes);
    }

    // ---------- cross-tool nesting ----------

    [Fact]
    public async Task MergeRoot_ExcludesNestedToolRoot()
    {
        Write(_home, "A\\b\\inner.txt", 30);  // B's declared root inside A
        Write(_home, "A\\own.txt", 20);

        var result = Scan(
            "{\"presets\":{},\"harnesses\":[" +
            "{\"id\":\"a\",\"name\":\"A\",\"type\":\"runtime\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\A\",\"kind\":\"sdk\",\"mode\":\"merge\"}]}," +
            "{\"id\":\"b\",\"name\":\"B\",\"type\":\"tooling\",\"roots\":[" +
            "{\"path\":\"%USERPROFILE%\\\\A\\\\b\",\"kind\":\"cache\"}]}]}");

        var a = result.Harnesses.Single(h => h.Id == "a");
        var b = result.Harnesses.Single(h => h.Id == "b");
        Assert.Equal(20, a.TotalBytes);   // A excludes B's bytes
        Assert.Equal(30, b.TotalBytes);   // B gets them
        Assert.DoesNotContain(a.Entries, e => e.Name == "b");
    }
}

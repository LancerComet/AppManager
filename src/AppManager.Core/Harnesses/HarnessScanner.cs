using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using AppManager.Core.Inventory;
using AppManager.Core.Models;
using AppManager.Core.Scanning;

namespace AppManager.Core.Harnesses;

/// <summary>
/// Measures disk usage of LLM-harness state (Claude Code, Codex, Antigravity…)
/// per <see cref="HarnessCatalog"/>. Program bytes are excluded from totals and
/// reported separately as ExcludedProgramBytes. Nested roots: a claimed path is
/// skipped inside parent measurements; claimed descendants are excluded from a
/// child measurement by walking down instead of measuring the whole subtree.
/// </summary>
public sealed class HarnessScanner
{
    // Same mask as FolderSizeCalculator: Offline, RecallOnOpen, RecallOnDataAccess.
    private const FileAttributes CloudPlaceholderAttributes =
        FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    private readonly HarnessCatalog _catalog;
    private readonly Func<string, IReadOnlyList<string>> _resolve;
    private readonly Func<string, string?> _env;
    private IReadOnlyList<string>? _guardBases;

    /// <param name="pathResolver">Maps a token name ("USERPROFILE", "APPDATA",
    /// "LOCALAPPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMDATA",
    /// "DRIVES") to base directories; most tokens return one dir, DRIVES
    /// returns every ready fixed drive root. Injected for tests.</param>
    /// <param name="envLookup">Reads an environment variable (used by root
    /// sources); defaults to Environment.GetEnvironmentVariable.</param>
    public HarnessScanner(
        HarnessCatalog? catalog = null,
        Func<string, IReadOnlyList<string>>? pathResolver = null,
        Func<string, string?>? envLookup = null)
    {
        _catalog = catalog ?? HarnessCatalog.Load();
        _resolve = pathResolver ?? DefaultResolve;
        _env = envLookup ?? Environment.GetEnvironmentVariable;
    }

    private static IReadOnlyList<string> DefaultResolve(string token) => token.ToUpperInvariant() switch
    {
        "USERPROFILE" => [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)],
        "APPDATA" => [Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)],
        "LOCALAPPDATA" => [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)],
        "PROGRAMFILES" => [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)],
        "PROGRAMFILES(X86)" => [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)],
        "PROGRAMDATA" => [Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)],
        "DRIVES" => DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList(),
        _ => throw new ArgumentException($"Unknown path token '{token}'."),
    };

    public Task<HarnessScanResult> ScanAsync(
        IReadOnlyList<InstalledApp>? apps = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(apps, cancellationToken), cancellationToken);

    /// <summary>A unit of measurement produced while classifying a root.</summary>
    private sealed class Unit
    {
        public required HarnessDefinition Harness { get; init; }
        public required string Path { get; init; }
        public required string Name { get; init; }
        public required HarnessKind Kind { get; set; }
        public required bool IsFile { get; init; }
        /// <summary>Version-set group key; units sharing it compete for "current".</summary>
        public object? VersionGroup { get; set; }

        public List<string> DirCandidates { get; } = new();
        public List<string> Files { get; } = new();

        public long SizeBytes { get; set; }
        public long FileCount { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
    }

    private HarnessScanResult Scan(IReadOnlyList<InstalledApp>? apps, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var watch = Stopwatch.StartNew();

        // ---- expand roots; dedupe resolved paths (first harness wins) ----
        var claims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expanded = new List<(HarnessDefinition Harness, HarnessRootDefinition Def, string Path, bool IsFile)>();
        foreach (var harness in _catalog.Harnesses)
        {
            foreach (var root in harness.Roots)
            {
                // Candidate order: default path first, then sources in listed
                // order; all pass through the same first-wins claims dedupe.
                var paths = new List<string>();
                if (root.Path is { } spec)
                    paths.AddRange(ExpandPathSpec(spec));
                foreach (var source in root.Sources)
                    paths.AddRange(ResolveSource(source).Where(IsSafeSourcePath));
                foreach (var resolved in paths)
                {
                    var norm = Normalize(resolved);
                    if (!claims.Add(norm))
                        continue;
                    expanded.Add((harness, root, resolved, File.Exists(resolved)));
                }
            }
        }

        // ---- classify + build measure units ----
        var units = new List<Unit>();
        foreach (var (harness, def, path, isFile) in expanded)
        {
            ct.ThrowIfCancellationRequested();
            if (isFile)
            {
                var kind = def.Kind;
                if (kind is HarnessKind.Versions or HarnessKind.VersionSet)
                    throw new InvalidDataException(
                        $"Harness '{harness.Id}': kind '{def.Kind}' is invalid on file root '{path}'.");
                units.Add(MakeUnit(harness, path, kind));
                continue;
            }

            if (def.Kind is HarnessKind.Versions or HarnessKind.VersionSet)
            {
                AddVersionSet(harness, units, path, claims, groupKey: path);
                continue;
            }

            if (def.Mode == HarnessRootMode.Merge)
            {
                // Direct children matching a rule split off as their own units;
                // claiming them makes BuildMeasurePlan exclude them from the
                // merged residual unit, which covers everything else.
                var matched = new List<(ChildEntry Child, HarnessChildRule Rule)>();
                foreach (var child in EnumerateChildren(path, claims))
                {
                    if (FirstMatch(def.Children, child.Name) is { } m)
                        matched.Add((child, m.Rule));
                }
                var mergedClaims = new HashSet<string>(claims, StringComparer.OrdinalIgnoreCase);
                foreach (var (child, _) in matched)
                {
                    var norm = Normalize(child.Path);
                    mergedClaims.Add(norm);
                    claims.Add(norm);
                }
                units.Add(MakeUnit(harness, path, def.Kind, mergedClaims,
                    nameOverride: def.Label));
                foreach (var (child, rule) in matched)
                {
                    var name = def.EntryPrefix is { } p ? $"{p} {child.Name}" : child.Name;
                    units.Add(MakeUnit(harness, child.Path, rule.Kind, claims, nameOverride: name));
                }
                continue;
            }

            var versionSets = new Dictionary<int, object>();
            foreach (var child in EnumerateChildren(path, claims))
            {
                var rule = FirstMatch(def.Children, child.Name);
                var kind = rule?.Rule.Kind ?? def.Kind;
                var name = def.EntryPrefix is { } prefix ? $"{prefix} {child.Name}" : null;
                switch (kind)
                {
                    case HarnessKind.Program:
                        units.Add(MakeUnit(harness, child.Path, HarnessKind.Program, claims, nameOverride: name));
                        break;
                    case HarnessKind.Versions:
                        if (child.IsFile)
                            throw new InvalidDataException(
                                $"Harness '{harness.Id}': kind 'versions' matched file '{child.Path}'.");
                        AddVersionSet(harness, units, child.Path, claims, groupKey: child.Path);
                        break;
                    case HarnessKind.VersionSet:
                    {
                        // Root kind VersionSet is handled before child enumeration,
                        // so a rule match is guaranteed here.
                        var ruleIndex = rule?.Index ?? -1;
                        if (!versionSets.TryGetValue(ruleIndex, out var key))
                            versionSets[ruleIndex] = key = new object();
                        var u = MakeUnit(harness, child.Path, HarnessKind.OldVersions, claims, nameOverride: name);
                        u.VersionGroup = key;
                        units.Add(u);
                        break;
                    }
                    default:
                        units.Add(MakeUnit(harness, child.Path, kind, claims, nameOverride: name));
                        break;
                }
            }
        }

        // ---- measure: one MeasureMany over all dir candidates + file infos ----
        var candidates = units.SelectMany(u => u.DirCandidates)
            .Select(p => new FolderCandidate { Path = p, Category = FolderCategory.Custom })
            .ToList();
        var candToUnit = new List<Unit>();
        foreach (var u in units)
            candToUnit.AddRange(Enumerable.Repeat(u, u.DirCandidates.Count));

        var usages = FolderSizeCalculator.MeasureMany(
            candidates, Environment.ProcessorCount, ct);

        for (int i = 0; i < candToUnit.Count; i++)
        {
            var u = candToUnit[i];
            var fu = usages[i];
            u.SizeBytes += fu.SizeBytes;
            u.FileCount += fu.FileCount;
            if (fu.LastWriteTimeUtc > u.LastWriteTimeUtc)
                u.LastWriteTimeUtc = fu.LastWriteTimeUtc;
        }
        foreach (var u in units)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var f in u.Files)
            {
                var (size, lastWrite) = FileMeasure(f);
                u.SizeBytes += size;
                u.FileCount += 1;
                if (lastWrite > u.LastWriteTimeUtc)
                    u.LastWriteTimeUtc = lastWrite;
            }
        }

        // ---- version sets: newest non-empty member is program ----
        foreach (var group in units.Where(u => u.VersionGroup is not null)
                     .GroupBy(u => u.VersionGroup!))
        {
            var nonEmpty = group.Where(u => u.SizeBytes > 0).ToList();
            if (nonEmpty.Count == 0)
            {
                foreach (var u in group)
                    u.Kind = HarnessKind.Program; // empty → ignored entirely
                continue;
            }
            var current = nonEmpty
                .OrderByDescending(u => u.LastWriteTimeUtc)
                .ThenBy(u => u.Path, StringComparer.OrdinalIgnoreCase)
                .First();
            foreach (var u in group)
                u.Kind = ReferenceEquals(u, current)
                    ? HarnessKind.Program
                    : u.SizeBytes > 0 ? HarnessKind.OldVersions : HarnessKind.Program;
        }

        // ---- assemble result ----
        var appsForIcons = apps;
        var harnessUsages = new List<HarnessUsage>();
        foreach (var harness in _catalog.Harnesses)
        {
            var hu = units.Where(u => u.Harness == harness).ToList();
            if (hu.Count == 0)
                continue;
            var usage = new HarnessUsage
            {
                Id = harness.Id,
                Name = harness.Name,
                Publisher = harness.Publisher,
                Type = harness.Type,
                ExcludedProgramBytes = hu.Where(u => u.Kind == HarnessKind.Program)
                    .Sum(u => u.SizeBytes),
            };
            usage.Entries = hu.Where(u => u.Kind != HarnessKind.Program)
                .OrderByDescending(u => u.SizeBytes)
                .Select(u => new HarnessEntry
                {
                    Path = u.Path,
                    Name = u.Name,
                    Kind = u.Kind,
                    SizeBytes = u.SizeBytes,
                    FileCount = u.FileCount,
                    LastWriteTimeUtc = u.LastWriteTimeUtc,
                    IsFile = u.IsFile,
                })
                .ToList();
            usage.TotalBytes = usage.Entries.Sum(e => e.SizeBytes);
            usage.BytesByKind = usage.Entries
                .GroupBy(e => e.Kind)
                .ToDictionary(g => g.Key, g => g.Sum(e => e.SizeBytes));

            // A tool with no measured data and no program bytes contributes
            // nothing — skip it (keeps tools with only program bytes).
            if (usage.TotalBytes == 0 && usage.ExcludedProgramBytes == 0)
                continue;

            if (harness.IconApp is { } pattern)
            {
                appsForIcons ??= new InstalledAppInventory().GetInstalledApps();
                var app = appsForIcons.FirstOrDefault(a =>
                    Regex.IsMatch(a.Name, pattern, RegexOptions.IgnoreCase));
                usage.IconAppPath = app?.IconPath;
            }
            harnessUsages.Add(usage);
        }

        watch.Stop();
        return new HarnessScanResult
        {
            Harnesses = harnessUsages.OrderByDescending(u => u.TotalBytes).ToList(),
            ScannedAtUtc = startedAt,
            Duration = watch.Elapsed,
        };
    }

    // ---- helpers ----

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnder(string candidate, string dir)
        => candidate.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
           candidate.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<string> ExpandPathSpec(string specPath)
    {
        var normalized = specPath.Replace('/', '\\');
        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var token = segments[0].Trim('%');
        foreach (var baseDir in _resolve(token))
        {
            if (string.IsNullOrEmpty(baseDir))
                continue;
            var last = segments[^1];
            if (last.Contains('*') || last.Contains('?'))
            {
                var parent = segments.Length == 1
                    ? baseDir
                    : Path.Combine(baseDir, Path.Combine(segments[1..^1]));
                if (!Directory.Exists(parent))
                    continue;
                foreach (var e in Directory.EnumerateFileSystemEntries(parent, last))
                    yield return e;
            }
            else
            {
                var resolved = Path.Combine(baseDir, Path.Combine(segments[1..]));
                if (File.Exists(resolved) || Directory.Exists(resolved))
                    yield return resolved;
            }
        }
    }

    /// <summary>Resolves the tokenized file path of an ini/xml source to the
    /// first existing file.</summary>
    private string? ResolveTokenFile(string specPath)
        => ExpandPathSpec(specPath).FirstOrDefault(File.Exists);

    /// <summary>Yields candidate directories contributed by a root source.
    /// Missing/unreadable files and variables yield nothing; the safety guard
    /// is applied by the caller.</summary>
    private IEnumerable<string> ResolveSource(HarnessSourceDefinition source)
    {
        switch (source.Type)
        {
            case HarnessSourceType.Env:
            {
                var value = _env(source.Env!);
                if (string.IsNullOrWhiteSpace(value))
                    yield break;
                foreach (var part in value.Split(';'))
                {
                    var p = ExpandVars(part.Trim().Trim('"').Trim('\''));
                    if (string.IsNullOrWhiteSpace(p))
                        continue;
                    yield return CombineSuffix(p, source.Suffix);
                }
                yield break;
            }
            case HarnessSourceType.Ini:
            {
                var file = ResolveTokenFile(source.FilePath!);
                if (file is null)
                    yield break;
                var value = ReadIniValue(file, source.Key!);
                if (value is not null)
                    yield return CombineSuffix(value, source.Suffix);
                yield break;
            }
            case HarnessSourceType.Xml:
            {
                var file = ResolveTokenFile(source.FilePath!);
                if (file is null)
                    yield break;
                var value = ReadXmlElement(file, source.Element!);
                if (value is not null)
                    yield return CombineSuffix(value, source.Suffix);
                yield break;
            }
        }
    }

    private static string CombineSuffix(string path, string? suffix)
        => string.IsNullOrEmpty(suffix) ? path : Path.Combine(path, suffix);

    /// <summary>Expands ${VAR} and %VAR% through the injected env lookup;
    /// unknown variables are left as-is (ExpandEnvironmentVariables semantics).</summary>
    private string ExpandVars(string value)
        => Regex.Replace(value, @"\$\{([^}]+)\}|%([^%]+)%",
            m => _env(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) ?? m.Value);

    /// <summary>
    /// Reads the first case-insensitive key=value match from an ini-style file.
    /// Only the requested key's value is returned — files like .npmrc may
    /// contain credentials on other lines, which are never read into results.
    /// </summary>
    private string? ReadIniValue(string file, string key)
    {
        try
        {
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                    continue;
                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                if (!line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;
                var value = line[(eq + 1)..].Trim().Trim('"').Trim('\'');
                return ExpandVars(value);
            }
        }
        catch
        {
        }
        return null;
    }

    private string? ReadXmlElement(string file, string localName)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreWhitespace = true,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(file, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && reader.LocalName == localName)
                {
                    var text = reader.ReadElementContentAsString().Trim();
                    return text.Length == 0 ? null : ExpandVars(text);
                }
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>
    /// Drops dangerous source-resolved paths: unrooted or nonexistent paths,
    /// drive roots, and anything equal to or above the well-known base dirs
    /// (a stray JAVA_HOME=C:\ must not measure the whole drive). Only applied
    /// to source candidates — the authored default paths stay as-is.
    /// </summary>
    private bool IsSafeSourcePath(string path)
    {
        if (!Path.IsPathRooted(path) || !Directory.Exists(path))
            return false;
        var norm = Normalize(path);
        var root = Path.GetPathRoot(norm);
        if (root is not null
            && Normalize(root).Equals(norm, StringComparison.OrdinalIgnoreCase))
            return false; // drive/share root
        foreach (var guard in GuardBases())
        {
            if (norm.Equals(guard, StringComparison.OrdinalIgnoreCase)
                || IsUnder(guard, norm))
                return false;
        }
        return true;
    }

    private IReadOnlyList<string> GuardBases()
    {
        if (_guardBases is not null)
            return _guardBases;
        var bases = new List<string>();
        foreach (var token in new[]
                 { "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMDATA" })
        {
            try
            {
                foreach (var b in _resolve(token))
                    if (!string.IsNullOrEmpty(b))
                        bases.Add(Normalize(b));
            }
            catch
            {
            }
        }
        foreach (var name in new[] { "WINDIR", "SystemRoot" })
        {
            var v = _env(name);
            if (!string.IsNullOrEmpty(v))
                bases.Add(Normalize(v));
        }
        return _guardBases = bases;
    }

    private sealed record ChildEntry(string Path, string Name, bool IsFile);

    /// <summary>Direct children of a dir; reparse points and claimed roots excluded.</summary>
    private static IEnumerable<ChildEntry> EnumerateChildren(string dir, HashSet<string> claims)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir).ToList();
        }
        catch
        {
            yield break;
        }
        foreach (var e in entries)
        {
            if (claims.Contains(Normalize(e)))
                continue;
            FileAttributes attrs;
            try { attrs = File.GetAttributes(e); }
            catch { continue; }
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                continue;
            yield return new ChildEntry(e, Path.GetFileName(e), (attrs & FileAttributes.Directory) == 0);
        }
    }

    private static (int Index, HarnessChildRule Rule)? FirstMatch(
        IReadOnlyList<HarnessChildRule> rules, string name)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            if (GlobMatch(rules[i].Match, name))
                return (i, rules[i]);
        }
        return null;
    }

    private static bool GlobMatch(string glob, string name)
        => Regex.IsMatch(name,
            "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase);

    /// <summary>Adds a version-set group: every direct child of dir is a member
    /// (files and dirs, claims/reparse excluded).</summary>
    private void AddVersionSet(
        HarnessDefinition harness, List<Unit> units, string dir,
        HashSet<string> claims, object groupKey)
    {
        foreach (var child in EnumerateChildren(dir, claims))
        {
            var u = MakeUnit(harness, child.Path, HarnessKind.OldVersions, claims);
            u.VersionGroup = groupKey;
            units.Add(u);
        }
    }

    private Unit MakeUnit(
        HarnessDefinition harness, string path, HarnessKind kind,
        HashSet<string>? claims = null, string? nameOverride = null)
    {
        var u = new Unit
        {
            Harness = harness,
            Path = path,
            Name = nameOverride ?? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, '/')),
            Kind = kind,
            IsFile = File.Exists(path) || !Directory.Exists(path),
        };
        if (u.IsFile)
        {
            u.Files.Add(path);
            return u;
        }
        BuildMeasurePlan(path, claims ?? new HashSet<string>(), u);
        return u;
    }

    /// <summary>If dir contains no claimed descendant → one candidate for the
    /// whole subtree; otherwise walk down excluding claimed paths.</summary>
    private static void BuildMeasurePlan(string dir, HashSet<string> claims, Unit u)
    {
        var normDir = Normalize(dir);
        if (!claims.Any(c => IsUnder(c, normDir)))
        {
            u.DirCandidates.Add(dir);
            return;
        }
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(d).ToList();
            }
            catch
            {
                continue;
            }
            foreach (var e in entries)
            {
                var norm = Normalize(e);
                if (claims.Contains(norm))
                    continue;
                FileAttributes attrs;
                try { attrs = File.GetAttributes(e); }
                catch { continue; }
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((attrs & FileAttributes.Directory) == 0)
                {
                    u.Files.Add(e);
                }
                else if (claims.Any(c => IsUnder(c, norm)))
                {
                    stack.Push(e);
                }
                else
                {
                    u.DirCandidates.Add(e);
                }
            }
        }
    }

    private static (long Size, DateTime LastWriteUtc) FileMeasure(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            var size = (fi.Attributes & CloudPlaceholderAttributes) != 0 ? 0 : fi.Length;
            return (size, fi.LastWriteTimeUtc);
        }
        catch
        {
            return (0, DateTime.MinValue);
        }
    }
}

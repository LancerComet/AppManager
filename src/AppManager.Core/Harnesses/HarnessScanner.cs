using System.Diagnostics;
using System.Text.RegularExpressions;
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
    private readonly Func<string, string> _resolve;

    /// <param name="pathResolver">Maps a token name ("USERPROFILE", "APPDATA",
    /// "LOCALAPPDATA") to a directory; defaults to Environment.GetFolderPath.
    /// Injected for tests.</param>
    public HarnessScanner(HarnessCatalog? catalog = null, Func<string, string>? pathResolver = null)
    {
        _catalog = catalog ?? HarnessCatalog.Load();
        _resolve = pathResolver ?? DefaultResolve;
    }

    private static string DefaultResolve(string token) => token.ToUpperInvariant() switch
    {
        "USERPROFILE" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "APPDATA" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LOCALAPPDATA" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
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
                foreach (var resolved in ExpandRoot(root.Path))
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

            var versionSets = new Dictionary<int, object>();
            foreach (var child in EnumerateChildren(path, claims))
            {
                var rule = FirstMatch(def.Children, child.Name);
                var kind = rule?.Rule.Kind ?? def.Kind;
                switch (kind)
                {
                    case HarnessKind.Program:
                        units.Add(MakeUnit(harness, child.Path, HarnessKind.Program, claims));
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
                        var u = MakeUnit(harness, child.Path, HarnessKind.OldVersions, claims);
                        u.VersionGroup = key;
                        units.Add(u);
                        break;
                    }
                    default:
                        units.Add(MakeUnit(harness, child.Path, kind, claims));
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

    private IEnumerable<string> ExpandRoot(string specPath)
    {
        var normalized = specPath.Replace('/', '\\');
        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var token = segments[0].Trim('%');
        var baseDir = _resolve(token);
        if (string.IsNullOrEmpty(baseDir))
            yield break;

        var last = segments[^1];
        if (last.Contains('*') || last.Contains('?'))
        {
            var parent = segments.Length == 1
                ? baseDir
                : Path.Combine(baseDir, Path.Combine(segments[1..^1]));
            if (!Directory.Exists(parent))
                yield break;
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
        HashSet<string>? claims = null)
    {
        var u = new Unit
        {
            Harness = harness,
            Path = path,
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, '/')),
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

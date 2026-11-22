using AppManager.Core.Inventory;
using AppManager.Core.Models;

namespace AppManager.Core.Matching;

/// <summary>
/// Pure attribution logic: assigns each measured folder to an installed app or a
/// known-tool rule. Performs no IO.
/// </summary>
public sealed class AppMatcher
{
    /// <summary>Install-location last segments too generic to identify an app.</summary>
    private static readonly HashSet<string> GenericInstallSegments = new(StringComparer.Ordinal)
    {
        "installer", "install", "setup", "bin", "app", "application", "program",
        "client", "current", "x64", "x86", "hub", "editor", "launcher", "update",
        "updater", "common", "shared", "data", "tools", "core", "main", "release",
        "latest", "default",
    };

    /// <summary>Folder names too generic for low-confidence name matching.</summary>
    private static readonly HashSet<string> GenericFolderNames = new(GenericInstallSegments, StringComparer.Ordinal)
    {
        "cache", "temp", "logs", "log", "config", "user", "users", "local",
        "packages", "plugins", "microsoft", "windows", "google", "crashpad",
        "crashdumps",
    };

    /// <summary>App names containing these are helper/installer-ish — lose to real apps.</summary>
    private static readonly string[] HelperTokens =
        { "installer", "updater", "uninstaller", "bootstrapper", "redistributable", "runtime", "helper" };

    private sealed class AppEntry
    {
        public required InstalledApp App;
        public string NormName = string.Empty;
        public string NormPublisher = string.Empty;
        public string? NormInstall;
        public string? NormInstallLastSegment;
        public string? RawInstallLastSegment; // keeps version digits — series disambiguation
        public string? NormIconExe;
        public bool HeuristicEligible; // non-system or registry app
        public bool IsHelper;
        public HashSet<string> Digits = new(StringComparer.Ordinal);
    }

    private readonly List<AppEntry> _apps;
    private readonly HashSet<string> _sharedInstallLocs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sharedIconExes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InstalledApp> _byPfn = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<InstalledApp>> _byPublisher = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<KnownTool>> _rulesByFolderName = new(StringComparer.OrdinalIgnoreCase);

    public AppMatcher(IReadOnlyList<InstalledApp> apps, IReadOnlyList<KnownTool> rules)
    {
        _apps = apps.Select(a =>
        {
            var normInstall = string.IsNullOrEmpty(a.InstallLocation)
                ? null
                : RegistryValueParser.NormalizeForCompare(a.InstallLocation!);
            string? lastSegment = null;
            string? rawLastSegment = null;
            if (normInstall is not null)
            {
                // Walk up past generic leaf segments ("Installer", "bin", …)
                // to the first segment that can identify the app.
                var segments = normInstall.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                for (var i = segments.Length - 1; i >= 0; i--)
                {
                    var normSeg = NameNormalizer.Normalize(segments[i]);
                    if (normSeg.Length == 0)
                        break; // leaf can't identify the app — don't climb past it
                    if (GenericInstallSegments.Contains(normSeg) && i > 0)
                        continue;
                    rawLastSegment = segments[i];
                    lastSegment = normSeg;
                    break;
                }
            }
            string? normIconExe = null;
            if (!string.IsNullOrEmpty(a.IconPath))
                normIconExe = NameNormalizer.Normalize(Path.GetFileNameWithoutExtension(a.IconPath));

            var normName = NameNormalizer.Normalize(a.Name);
            return new AppEntry
            {
                App = a,
                NormName = normName,
                NormPublisher = NameNormalizer.Normalize(a.Publisher),
                NormInstall = normInstall,
                NormInstallLastSegment = lastSegment,
                RawInstallLastSegment = rawLastSegment,
                NormIconExe = normIconExe,
                HeuristicEligible = a.Source == AppSource.Registry || !a.IsSystem,
                IsHelper = HelperTokens.Any(t => normName.Contains(t, StringComparison.Ordinal)),
                Digits = DigitTokens(a.Name),
            };
        }).ToList();

        // Install locations shared by two or more distinct apps are container
        // dirs (bad registry data or a launcher root) — attributing them to one
        // app would hand it other apps' bytes.
        var locOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _apps)
        {
            if (e.NormInstall is null)
                continue;
            if (!locOwners.TryGetValue(e.NormInstall, out var owners))
                locOwners[e.NormInstall] = owners = new HashSet<string>(StringComparer.Ordinal);
            owners.Add(e.App.Id);
        }
        foreach (var kv in locOwners)
            if (kv.Value.Count >= 2)
                _sharedInstallLocs.Add(kv.Key);

        // Icon exe names shared by two or more distinct apps are launcher
        // helpers (e.g. EALaunchHelper.exe on every EA game) — ambiguous.
        var iconOwners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var e in _apps)
        {
            if (e.NormIconExe is null || e.NormIconExe.Length < 3)
                continue;
            if (!iconOwners.TryGetValue(e.NormIconExe, out var owners))
                iconOwners[e.NormIconExe] = owners = new HashSet<string>(StringComparer.Ordinal);
            owners.Add(e.App.Id);
        }
        foreach (var kv in iconOwners)
            if (kv.Value.Count >= 2)
                _sharedIconExes.Add(kv.Key);

        foreach (var a in apps)
        {
            if (!string.IsNullOrEmpty(a.PackageFamilyName))
                _byPfn[a.PackageFamilyName] = a;
        }

        foreach (var group in _apps.Where(e => e.NormPublisher.Length > 0).GroupBy(e => e.NormPublisher))
            _byPublisher[group.Key] = group.Select(e => e.App).ToList();

        foreach (var rule in rules)
        {
            foreach (var folderName in rule.Folders)
            {
                if (!_rulesByFolderName.TryGetValue(folderName, out var list))
                    _rulesByFolderName[folderName] = list = new List<KnownTool>();
                if (!list.Contains(rule))
                    list.Add(rule);
            }
        }
    }

    public List<AttributedFolder> Match(IReadOnlyList<FolderUsage> folders)
    {
        var result = new List<AttributedFolder>(folders.Count);
        foreach (var folder in folders)
            result.Add(MatchOne(folder));
        return result;
    }

    private AttributedFolder MatchOne(FolderUsage folder)
    {
        var normPath = RegistryValueParser.NormalizeForCompare(folder.FullPath);

        // 1. PackageData folder named exactly like an MSIX PackageFamilyName.
        if (folder.Category == FolderCategory.PackageData &&
            _byPfn.TryGetValue(folder.Name, out var pfnApp))
        {
            return new AttributedFolder
            {
                Folder = folder,
                App = pfnApp,
                Confidence = MatchConfidence.High,
                Reason = "包数据目录 (PFN 匹配)",
            };
        }

        // 2. Folder equals an app's install location (exact), then contains it.
        // Shared locations (>=2 distinct apps) are container dirs — ambiguous,
        // so they never produce an install match on their own.
        var exact = _apps.FirstOrDefault(e => e.NormInstall is not null &&
            !_sharedInstallLocs.Contains(e.NormInstall) &&
            normPath.Equals(e.NormInstall, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return InstallDirHit(folder, exact.App);

        // Containment is only unambiguous when exactly one distinct app's
        // install location lives under this folder.
        var containedIds = new HashSet<string>(StringComparer.Ordinal);
        AppEntry? contained = null;
        foreach (var e in _apps)
        {
            if (e.NormInstall is null || _sharedInstallLocs.Contains(e.NormInstall))
                continue;
            if (!e.NormInstall.StartsWith(normPath + "\\", StringComparison.OrdinalIgnoreCase))
                continue;
            if (containedIds.Add(e.App.Id))
                contained = e;
            if (containedIds.Count > 1)
                break;
        }
        if (containedIds.Count == 1 && contained is not null)
            return InstallDirHit(folder, contained.App);

        // 3. Known tool rules.
        if (_rulesByFolderName.TryGetValue(folder.Name, out var candidates))
        {
            // Rules may require a specific parent directory (e.g. "Shared" only
            // under "JetBrains").
            var parentName = ParentDirectoryName(folder.FullPath);
            candidates = candidates
                .Where(r => r.Parent is null ||
                            r.Parent.Equals(parentName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Prefer a rule scoped to this category, then an unscoped catch-all.
            var rule = candidates.FirstOrDefault(r => r.Scopes.Contains(folder.Category))
                       ?? candidates.FirstOrDefault(r => r.Scopes.Count == 0);
            if (rule is not null)
            {
                var app = FindAppByKeywords(rule.Keywords, folder.Name);
                if (app is not null)
                {
                    return new AttributedFolder
                    {
                        Folder = folder,
                        App = app.App,
                        Rule = rule,
                        Confidence = MatchConfidence.High,
                        Reason = $"已知规则: {folder.Name}",
                    };
                }
                return new AttributedFolder
                {
                    Folder = folder,
                    Rule = rule,
                    Confidence = MatchConfidence.Medium,
                    Reason = $"已知规则: {folder.Name}",
                };
            }
        }

        // 4. Name heuristics (never against IsSystem MSIX apps).
        var fn = NameNormalizer.Normalize(folder.Name);
        if (fn.Length >= 3)
        {
            // Raw last-segment equality keeps version digits, so
            // "Battlefield 6" lands on BF6 rather than any "battlefield*" app.
            foreach (var e in _apps)
            {
                if (!e.HeuristicEligible || e.RawInstallLastSegment is null ||
                    _sharedInstallLocs.Contains(e.NormInstall!))
                    continue;
                if (folder.Name.Trim().Equals(e.RawInstallLastSegment, StringComparison.OrdinalIgnoreCase))
                    return NamedHit(folder, e.App, "名称匹配: 安装目录名");
            }
            foreach (var e in _apps)
            {
                if (!e.HeuristicEligible)
                    continue;
                if (fn == e.NormName)
                    return NamedHit(folder, e.App, "名称匹配: 应用名");
            }
            foreach (var e in _apps)
            {
                if (!e.HeuristicEligible)
                    continue;
                if (e.NormInstallLastSegment is not null &&
                    !_sharedInstallLocs.Contains(e.NormInstall!) &&
                    fn == e.NormInstallLastSegment)
                    return NamedHit(folder, e.App, "名称匹配: 安装目录名");
                if (e.NormIconExe is not null && e.NormIconExe.Length >= 3 &&
                    !_sharedIconExes.Contains(e.NormIconExe) && fn == e.NormIconExe)
                    return NamedHit(folder, e.App, "名称匹配: 程序文件名");
            }

            // Publisher folder with exactly one app.
            if (_byPublisher.TryGetValue(fn, out var publisherApps) && publisherApps.Count == 1)
            {
                var app = publisherApps[0];
                var entry = _apps.FirstOrDefault(e => e.App == app);
                if (entry is { HeuristicEligible: true })
                {
                    return new AttributedFolder
                    {
                        Folder = folder,
                        App = app,
                        Confidence = MatchConfidence.Medium,
                        Reason = "发布者文件夹",
                    };
                }
            }

            // Low-confidence prefix match in either direction (no mid-string
            // containment — too noisy); prefer shorter app names on ties.
            if (fn.Length >= 5 && !GenericFolderNames.Contains(fn))
            {
                AppEntry? best = null;
                foreach (var e in _apps)
                {
                    if (!e.HeuristicEligible || e.NormName.Length < 4)
                        continue;
                    var related = e.NormName.StartsWith(fn, StringComparison.Ordinal) ||
                                  fn.StartsWith(e.NormName, StringComparison.Ordinal);
                    if (!related)
                        continue;
                    if (best is null || e.NormName.Length < best.NormName.Length)
                        best = e;
                }
                if (best is not null)
                {
                    return new AttributedFolder
                    {
                        Folder = folder,
                        App = best.App,
                        Confidence = MatchConfidence.Low,
                        Reason = "名称相近",
                    };
                }
            }
        }

        // 5. Unattributed.
        return new AttributedFolder
        {
            Folder = folder,
            Confidence = MatchConfidence.None,
            Reason = string.Empty,
        };
    }

    private static AttributedFolder InstallDirHit(FolderUsage folder, InstalledApp app)
        => new()
        {
            Folder = folder,
            App = app,
            Confidence = MatchConfidence.High,
            Reason = "安装目录匹配",
        };

    private static AttributedFolder NamedHit(FolderUsage folder, InstalledApp app, string reason)
        => new()
        {
            Folder = folder,
            App = app,
            Confidence = MatchConfidence.High,
            Reason = reason,
        };

    /// <summary>
    /// Finds the installed app whose normalized name contains a rule keyword.
    /// Helper apps (installers/updaters/…) lose to real apps; digit tokens
    /// shared with the folder name ("2022") break remaining ties, then exact
    /// normalized equality, longest keyword, shortest app name.
    /// </summary>
    private AppEntry? FindAppByKeywords(IReadOnlyList<string> keywords, string folderName)
    {
        var normKeywords = keywords
            .Select(NameNormalizer.Normalize)
            .Where(k => k.Length >= 3)
            .ToList();
        var folderDigits = DigitTokens(folderName);

        var candidates = new List<(AppEntry Entry, int BestKw, bool Exact, int DigitOverlap)>();
        foreach (var e in _apps)
        {
            if (e.NormName.Length == 0)
                continue;
            var localBest = -1;
            foreach (var nkw in normKeywords)
            {
                if (e.NormName.Contains(nkw, StringComparison.Ordinal) && nkw.Length > localBest)
                    localBest = nkw.Length;
            }
            if (localBest < 0)
                continue;

            var isExact = normKeywords.Any(k => k == e.NormName);
            var digitOverlap = folderDigits.Count == 0 || e.Digits.Count == 0
                ? 0
                : folderDigits.Count(e.Digits.Contains);
            candidates.Add((e, localBest, isExact, digitOverlap));
        }

        if (candidates.Count == 0)
            return null;

        var nonHelpers = candidates.Where(c => !c.Entry.IsHelper).ToList();
        if (nonHelpers.Count > 0)
            candidates = nonHelpers;

        var maxOverlap = candidates.Max(c => c.DigitOverlap);
        if (maxOverlap > 0)
            candidates = candidates.Where(c => c.DigitOverlap == maxOverlap).ToList();

        return candidates
            .OrderByDescending(c => c.Exact)
            .ThenByDescending(c => c.BestKw)
            .ThenBy(c => c.Entry.NormName.Length)
            .First().Entry;
    }

    /// <summary>Runs of digits in a name — used to prefer the matching series version.</summary>
    private static HashSet<string> DigitTokens(string? name)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(name))
            return set;
        var start = -1;
        for (var i = 0; i <= name.Length; i++)
        {
            var isDigit = i < name.Length && char.IsDigit(name[i]);
            if (isDigit && start < 0)
                start = i;
            else if (!isDigit && start >= 0)
            {
                set.Add(name[start..i]);
                start = -1;
            }
        }
        return set;
    }

    private static string? ParentDirectoryName(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(dir) ? null : Path.GetFileName(dir);
        }
        catch
        {
            return null;
        }
    }
}

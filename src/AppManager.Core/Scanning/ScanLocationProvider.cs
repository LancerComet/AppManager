using System.IO.Enumeration;
using System.Runtime.InteropServices;
using AppManager.Core.Inventory;
using AppManager.Core.Matching;
using AppManager.Core.Models;

namespace AppManager.Core.Scanning;

public sealed record FolderCandidate
{
    public required string Path { get; init; }
    public required FolderCategory Category { get; init; }

    public override string ToString() => $"{Category}: {Path}";
}

/// <summary>
/// Discovers the top-level folders to measure: user home children, AppData roots,
/// ProgramData, Program Files, package data, per-app install dirs and custom roots.
/// The result is guaranteed non-overlapping (no candidate is an ancestor of another).
/// </summary>
public static class ScanLocationProvider
{
    private static readonly HashSet<string> ProgramFilesExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files",
        "WindowsApps",
        "ModifiableWindowsApps",
        "Internet Explorer",
        "Windows Defender",
        "Windows Defender Advanced Threat Protection",
        "Windows Mail",
        "Windows Media Player",
        "Windows NT",
        "Windows Photo Viewer",
        "Windows Portable Devices",
        "Windows Security",
        "Windows Sidebar",
        "WindowsPowerShell",
        "Microsoft Update Health Tools",
        "Reference Assemblies",
        "MSBuild",
        "Uninstall Information",
    };

    public static List<FolderCandidate> Discover(IReadOnlyList<InstalledApp> apps, ScanOptions options)
    {
        var candidates = new List<FolderCandidate>();

        var publisherCounts = apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Publisher))
            .Select(a => NameNormalizer.Normalize(a.Publisher))
            .Where(n => n.Length > 0)
            .GroupBy(n => n)
            .ToDictionary(g => g.Key, g => g.Count());

        var installLocations = apps
            .Where(a => !string.IsNullOrEmpty(a.InstallLocation))
            .Select(a => (App: a, Loc: RegistryValueParser.NormalizeForCompare(a.InstallLocation!)))
            .ToList();

        // --- %USERPROFILE% children -----------------------------------------
        var home = NormalizeEnv("USERPROFILE", Environment.SpecialFolder.UserProfile);
        var homeSkip = BuildHomeSkipSet(home);
        foreach (var dir in EnumerateDirs(home))
        {
            if (homeSkip.Contains(Normalize(dir)))
                continue;
            var name = Path.GetFileName(dir);
            if (name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SkyDrive", StringComparison.OrdinalIgnoreCase))
                continue;
            candidates.Add(new FolderCandidate
            {
                Path = dir,
                Category = name.StartsWith('.') ? FolderCategory.UserHomeDot : FolderCategory.UserHome,
            });
        }

        // --- Documents / Saved Games (resolved; may point to OneDrive or D:) --
        var documents = NormalizeSafe(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        var savedGames = GetKnownFolderPath(KnownFolderGuids.SavedGames);
        AddChildren(candidates, documents, FolderCategory.Documents);
        AddChildren(candidates, IsLocalPath(savedGames) ? savedGames : null, FolderCategory.SavedGames);

        // --- AppData roots ---------------------------------------------------
        var local = NormalizeEnv("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData);
        foreach (var dir in EnumerateDirs(local))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("Packages", StringComparison.OrdinalIgnoreCase))
                AddChildren(candidates, dir, FolderCategory.PackageData);
            else if (name.Equals("Programs", StringComparison.OrdinalIgnoreCase))
                AddChildren(candidates, dir, FolderCategory.LocalPrograms);
            else
                AddAppDataChild(candidates, dir, FolderCategory.LocalAppData, publisherCounts, expandMicrosoft: true);
        }

        var roaming = NormalizeEnv("APPDATA", Environment.SpecialFolder.ApplicationData);
        foreach (var dir in EnumerateDirs(roaming))
            AddAppDataChild(candidates, dir, FolderCategory.RoamingAppData, publisherCounts, expandMicrosoft: true);

        var localLow = Combine(home, @"AppData\LocalLow");
        foreach (var dir in EnumerateDirs(localLow))
            AddAppDataChild(candidates, dir, FolderCategory.LocalLowAppData, publisherCounts, expandMicrosoft: false);

        var programData = NormalizeEnv("ProgramData", Environment.SpecialFolder.CommonApplicationData);
        foreach (var dir in EnumerateDirs(programData))
            AddAppDataChild(candidates, dir, FolderCategory.ProgramData, publisherCounts, expandMicrosoft: true);

        // --- Program Files roots ----------------------------------------------
        foreach (var pf in ProgramFilesRoots())
        {
            foreach (var dir in EnumerateDirs(pf))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals("Common Files", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var sub in EnumerateDirs(dir))
                        AddProgramDir(candidates, sub, installLocations);
                }
                else if (!ProgramFilesExclusions.Contains(name))
                {
                    AddProgramDir(candidates, dir, installLocations);
                }
            }
        }

        // --- Per-app install dirs ----------------------------------------------
        foreach (var entry in installLocations)
        {
            var app = entry.App;
            if (app.IsSystem)
                continue; // MSIX system packages: skip
            var loc = entry.Loc;
            if (!Directory.Exists(loc))
                continue;
            if (IsReparsePoint(loc))
                continue;
            candidates.Add(new FolderCandidate { Path = loc, Category = FolderCategory.InstallDir });
        }

        // --- Custom roots -------------------------------------------------------
        foreach (var extra in options.ExtraRoots)
            AddChildren(candidates, NormalizeSafe(extra), FolderCategory.Custom);

        return CollapseOverlaps(candidates);
    }

    private static void AddAppDataChild(
        List<FolderCandidate> candidates,
        string dir,
        FolderCategory category,
        Dictionary<string, int> publisherCounts,
        bool expandMicrosoft)
    {
        var name = Path.GetFileName(dir);
        if (expandMicrosoft && name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            AddChildren(candidates, dir, category);
            return;
        }

        // Vendor folder: same normalized name as a publisher of >= 2 apps.
        var normalized = NameNormalizer.Normalize(name);
        if (normalized.Length > 0 &&
            publisherCounts.TryGetValue(normalized, out var count) && count >= 2)
        {
            AddChildren(candidates, dir, category);
            return;
        }

        candidates.Add(new FolderCandidate { Path = dir, Category = category });
    }

    /// <summary>
    /// Adds a Program Files-style directory as an InstallDir candidate. When it
    /// contains the install locations of two or more different apps (a vendor
    /// container like "Microsoft Office"), it is expanded one level instead.
    /// </summary>
    private static void AddProgramDir(
        List<FolderCandidate> candidates,
        string dir,
        List<(InstalledApp App, string Loc)> installLocations)
    {
        var normalized = Normalize(dir);
        var distinctApps = installLocations
            .Where(e => e.Loc.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                        e.Loc.StartsWith(normalized + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.App.Id)
            .Distinct()
            .Count();

        if (distinctApps >= 2)
        {
            foreach (var sub in EnumerateDirs(dir))
                candidates.Add(new FolderCandidate { Path = sub, Category = FolderCategory.InstallDir });
        }
        else
        {
            candidates.Add(new FolderCandidate { Path = dir, Category = FolderCategory.InstallDir });
        }
    }

    private static void AddChildren(List<FolderCandidate> candidates, string? parent, FolderCategory category)
    {
        if (string.IsNullOrEmpty(parent) || !IsLocalPath(parent))
            return;
        foreach (var dir in EnumerateDirs(parent))
            candidates.Add(new FolderCandidate { Path = dir, Category = category });
    }

    /// <summary>
    /// Removes duplicate paths (case-insensitive) and any candidate nested inside
    /// another candidate (the ancestor is kept).
    /// </summary>
    public static List<FolderCandidate> CollapseOverlaps(IEnumerable<FolderCandidate> candidates)
    {
        var deduped = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Path))
            .Select(c => new FolderCandidate { Path = Normalize(c.Path), Category = c.Category })
            .DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.Path.Length)
            .ToList();

        var kept = new List<FolderCandidate>();
        foreach (var c in deduped)
        {
            var nested = false;
            foreach (var k in kept)
            {
                if (c.Path.Length > k.Path.Length &&
                    c.Path.StartsWith(k.Path + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    nested = true;
                    break;
                }
            }
            if (!nested)
                kept.Add(c);
        }
        return kept;
    }

    private static HashSet<string> BuildHomeSkipSet(string home)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p)
        {
            if (!string.IsNullOrEmpty(p))
                set.Add(Normalize(p));
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.Personal));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Favorites));
        Add(GetKnownFolderPath(KnownFolderGuids.Downloads));
        Add(GetKnownFolderPath(KnownFolderGuids.SavedGames));
        Add(GetKnownFolderPath(KnownFolderGuids.Links));
        Add(GetKnownFolderPath(KnownFolderGuids.Contacts));
        Add(GetKnownFolderPath(KnownFolderGuids.Searches));
        Add(GetKnownFolderPath(KnownFolderGuids.SavedSearches));
        Add(GetKnownFolderPath(KnownFolderGuids.Objects3D));
        Add(Combine(home, "AppData"));
        Add(home);
        return set;
    }

    private static IEnumerable<string> EnumerateDirs(string? parent)
    {
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            yield break;

        // EnumerateDirectories is lazy — materialize inside the try so an
        // access-denied root does not throw mid-iteration.
        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(parent).ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            FileAttributes attrs;
            try
            {
                attrs = File.GetAttributes(dir);
            }
            catch
            {
                continue;
            }
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                continue;
            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) ==
                (FileAttributes.Hidden | FileAttributes.System))
                continue;
            yield return dir;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsLocalPath(string? path)
        => !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) &&
           !path.StartsWith("\\\\") && !path.StartsWith("//");

    private static IEnumerable<string> ProgramFilesRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramW6432"),
        })
        {
            var n = NormalizeSafe(p);
            if (n.Length > 0 && seen.Add(n) && Directory.Exists(n))
                yield return n;
        }
    }

    private static string NormalizeEnv(string envName, Environment.SpecialFolder fallback)
    {
        var env = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(env))
            return Normalize(env);
        return NormalizeSafe(Environment.GetFolderPath(fallback));
    }

    private static string NormalizeSafe(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : Normalize(path);

    private static string Normalize(string path)
        => RegistryValueParser.NormalizeForCompare(path);

    private static string Combine(string a, string b)
        => a.Length == 0 ? string.Empty : Path.Combine(a, b);

    private static class KnownFolderGuids
    {
        public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
        public static readonly Guid SavedGames = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");
        public static readonly Guid Links = new("4D9F7874-4E0C-4904-967B-40B0D20C3E4B");
        public static readonly Guid Contacts = new("56784854-C6CB-462B-8169-88E350ACB882");
        public static readonly Guid Searches = new("7D1D3A04-DEBB-4115-95CF-2F29DA2920DA");
        public static readonly Guid SavedSearches = new("7D1D3A04-DEBB-4115-95CF-2F29DA2920DA");
        public static readonly Guid Objects3D = new("31C0DD25-9439-4F12-BF41-7FF4EDA38722");
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    private static string? GetKnownFolderPath(Guid id)
    {
        try
        {
            var hr = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr);
            if (hr != 0 || ptr == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
        catch
        {
            return null;
        }
    }
}

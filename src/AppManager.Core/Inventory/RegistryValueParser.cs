namespace AppManager.Core.Inventory;

/// <summary>
/// Pure helpers for parsing registry uninstall values (DisplayIcon, InstallLocation,
/// UninstallString) into usable filesystem paths.
/// </summary>
public static class RegistryValueParser
{
    /// <summary>
    /// Parses a DisplayIcon value: strips quotes, strips a trailing ",&lt;index&gt;",
    /// expands environment variables. Returns null when the result is not a rooted path.
    /// Does NOT check file existence (caller decides on fallbacks).
    /// </summary>
    public static string? ParseIconPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim();

        // Strip the quoted part if present, keeping any suffix for the index strip.
        if (s.Length >= 2 && s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            if (end > 0)
                s = string.Concat(s.AsSpan(1, end - 1), s.AsSpan(end + 1));
        }

        s = s.Trim();

        // Strip trailing ",<int>" icon index (e.g. ",0", ",-3").
        var comma = s.LastIndexOf(',');
        if (comma > 0)
        {
            var tail = s[(comma + 1)..].Trim();
            if (tail.Length > 0 && int.TryParse(tail, out _))
                s = s[..comma].Trim().Trim('"').Trim();
        }

        if (s.Length == 0)
            return null;

        s = Environment.ExpandEnvironmentVariables(s);

        if (!Path.IsPathRooted(s))
            return null;

        try
        {
            s = Path.GetFullPath(s);
        }
        catch
        {
            return null;
        }

        return s;
    }

    /// <summary>
    /// Extracts the executable path from an UninstallString value.
    /// Handles quoted paths, unquoted "path.exe arg1 arg2" forms and env vars.
    /// Returns null when no rooted .exe path is found.
    /// </summary>
    public static string? ExtractExePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = Environment.ExpandEnvironmentVariables(raw.Trim());
        string? exe = null;

        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end > 1)
                exe = s[1..end];
        }
        else
        {
            var i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
                exe = s[..(i + 4)];
        }

        if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            exe = Path.GetFullPath(exe);
        }
        catch
        {
            return null;
        }

        return Path.IsPathRooted(exe) ? exe : null;
    }

    /// <summary>
    /// Normalizes an InstallLocation value: expands env vars, trims quotes/trailing
    /// slashes, and rejects locations that would attribute an entire tree to one app
    /// (drive roots, Windows dir or anything under it, Program Files roots themselves,
    /// user profile / AppData / ProgramData roots, LocalAppData\Programs itself).
    /// </summary>
    public static string? NormalizeInstallLocation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string s;
        try
        {
            s = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')).Trim();
            s = Path.GetFullPath(s);
        }
        catch
        {
            return null;
        }

        s = s.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (s.Length == 0 || !Path.IsPathRooted(s))
            return null;

        return IsRejectedLocation(s) ? null : s;
    }

    /// <summary>
    /// If <paramref name="iconPath"/> is an .exe inside a first-level-or-deeper subfolder
    /// of a Program Files root or %LOCALAPPDATA%\Programs, returns that first-level
    /// subfolder (e.g. C:\Program Files\Foo\bin\foo.exe -&gt; C:\Program Files\Foo).
    /// </summary>
    public static string? DeriveInstallLocationFromIcon(string? iconPath)
    {
        if (string.IsNullOrEmpty(iconPath) ||
            !iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        return FirstLevelUnderInstallRoot(iconPath);
    }

    /// <summary>
    /// Launcher exes that sit next to (not inside) the apps they manage — deriving
    /// an install dir from their UninstallString would attribute the launcher
    /// directory to the app (e.g. "steam.exe" -&gt; the whole Steam library).
    /// </summary>
    private static readonly HashSet<string> LauncherExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamcmd.exe", "epicgameslauncher.exe", "eadesktop.exe",
        "origin.exe", "upc.exe", "ubisoftconnect.exe", "galaxyclient.exe",
        "battle.net.exe",
    };

    /// <summary>
    /// Derives an install directory from an UninstallString value. Rejects
    /// msiexec/rundll32-based strings, protocol-invocation uninstallers
    /// ("steam.exe" steam://uninstall/...) and known game-launcher exes.
    /// If the uninstaller exe lives under a Program Files root or
    /// %LOCALAPPDATA%\Programs, returns the first-level subfolder; for other
    /// local dirs returns the exe's directory.
    /// </summary>
    public static string? DeriveInstallLocationFromUninstallString(string? raw)
    {
        if (raw is not null && raw.Contains("://", StringComparison.Ordinal))
            return null; // launcher invoked via protocol URI, not the app dir
        var exe = ExtractExePath(raw);
        if (exe is null)
            return null;

        var name = Path.GetFileName(exe);
        if (name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) ||
            LauncherExes.Contains(name))
            return null;

        var firstLevel = FirstLevelUnderInstallRoot(exe);
        if (firstLevel is not null)
            return firstLevel;

        var dir = Path.GetDirectoryName(exe);
        if (dir is null)
            return null;

        return IsRejectedLocation(dir) ? null : NormalizeForCompare(dir);
    }

    /// <summary>
    /// If <paramref name="filePath"/> lives under a Program Files root or
    /// %LOCALAPPDATA%\Programs, returns the first-level subfolder beneath that
    /// root (null when the file sits directly in the root or elsewhere).
    /// </summary>
    public static string? FirstLevelUnderInstallRoot(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        var normalized = NormalizeForCompare(filePath);
        foreach (var root in InstallRoots())
        {
            if (root.Length == 0)
                continue;
            if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            var relative = normalized[root.Length..];
            var sep = relative.IndexOfAny(new[] { '\\', '/' });
            if (sep <= 0)
                continue; // file sits directly in the root, no subfolder to attribute
            var candidate = Path.Combine(root.TrimEnd('\\', '/'), relative[..sep]);
            return IsRejectedLocation(candidate) ? null : candidate;
        }

        return null;
    }

    internal static bool IsRejectedLocation(string fullPath)
    {
        var p = NormalizeForCompare(fullPath);

        // Drive root (e.g. C:\)
        if (p.Length <= 3)
            return true;

        // %WINDIR% or anything under it
        var windir = NormalizeForCompare(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (windir.Length > 0 && (p.Equals(windir, StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(windir + "\\", StringComparison.OrdinalIgnoreCase)))
            return true;

        foreach (var root in RejectedRoots())
        {
            if (root.Length == 0)
                continue;
            if (p.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static string NormalizeForCompare(string path)
    {
        string s;
        try
        {
            s = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch
        {
            s = path;
        }
        return s.TrimEnd('\\', '/');
    }

    private static IEnumerable<string> RejectedRoots()
    {
        yield return Env("WINDIR");
        yield return Env("ProgramFiles");
        yield return Env("ProgramFiles(x86)");
        yield return Env("ProgramW6432");
        yield return Env("CommonProgramFiles");
        yield return Env("CommonProgramFiles(x86)");
        yield return Env("CommonProgramW6432");
        yield return Env("USERPROFILE");
        yield return Env("APPDATA");
        yield return Env("LOCALAPPDATA");
        yield return Env("ProgramData");
        yield return NormalizeForCompareSafe(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        yield return NormalizeForCompareSafe(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        yield return NormalizeForCompareSafe(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        yield return NormalizeForCompareSafe(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        var user = Env("USERPROFILE");
        if (user.Length > 0)
        {
            yield return user + "\\AppData";
            yield return user + "\\AppData\\LocalLow";
        }
        var local = Env("LOCALAPPDATA");
        if (local.Length > 0)
            yield return local + "\\Programs";
    }

    private static IEnumerable<string> InstallRoots()
    {
        foreach (var r in new[]
        {
            Env("ProgramFiles"),
            Env("ProgramFiles(x86)"),
            Env("ProgramW6432"),
        })
        {
            if (r.Length > 0)
                yield return r + "\\";
        }
        var local = Env("LOCALAPPDATA");
        if (local.Length > 0)
            yield return local + "\\Programs\\";
    }

    private static string Env(string name)
        => NormalizeForCompareSafe(Environment.GetEnvironmentVariable(name));

    private static string NormalizeForCompareSafe(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : NormalizeForCompare(value);
}

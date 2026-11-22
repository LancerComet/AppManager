using AppManager.Core.Matching;
using Microsoft.Win32;

namespace AppManager.Core.Inventory;

/// <summary>
/// Maps App Paths registry entries (SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths)
/// to install directories. Looked up by app name via NameNormalizer equality against
/// the subkey name (e.g. "chrome.exe" -&gt; "chrome").
/// </summary>
public sealed class AppPathsLocator
{
    private const string AppPathsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    // normalized key-name stem -> preferred directory
    private readonly IReadOnlyDictionary<string, string> _map;

    public AppPathsLocator(IReadOnlyDictionary<string, string> normalizedNameToDir)
        => _map = normalizedNameToDir;

    public static AppPathsLocator Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, map);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, map);
        Read(RegistryHive.CurrentUser, RegistryView.Registry64, map);
        return new AppPathsLocator(map);
    }

    public string? FindInstallDir(string appName)
    {
        var key = NameNormalizer.Normalize(appName);
        return key.Length >= 3 && _map.TryGetValue(key, out var dir) ? dir : null;
    }

    private static void Read(RegistryHive hive, RegistryView view, Dictionary<string, string> map)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var appPaths = baseKey.OpenSubKey(AppPathsPath);
            if (appPaths is null)
                return;

            foreach (var subName in appPaths.GetSubKeyNames())
            {
                try
                {
                    using var key = appPaths.OpenSubKey(subName);
                    if (key is null)
                        continue;

                    var stem = subName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? subName[..^4]
                        : subName;
                    var normalized = NameNormalizer.Normalize(stem);
                    if (normalized.Length < 3 || map.ContainsKey(normalized))
                        continue;

                    // Prefer the explicit "Path" value, else the default value's exe dir.
                    var pathValue = RegistryValueParser.NormalizeInstallLocation(key.GetValue("Path") as string);
                    var dir = pathValue;
                    if (dir is null)
                    {
                        var exe = RegistryValueParser.ExtractExePath(key.GetValue(null) as string);
                        if (exe is not null)
                        {
                            var exeDir = Path.GetDirectoryName(exe);
                            if (exeDir is not null && !RegistryValueParser.IsRejectedLocation(exeDir))
                                dir = RegistryValueParser.NormalizeForCompare(exeDir);
                        }
                    }

                    if (dir is not null)
                        map[normalized] = dir;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}

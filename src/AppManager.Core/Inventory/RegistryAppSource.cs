using System.Text.RegularExpressions;
using AppManager.Core.Models;
using Microsoft.Win32;

namespace AppManager.Core.Inventory;

public interface IInstalledAppSource
{
    IReadOnlyList<InstalledApp> GetInstalledApps();
}

/// <summary>
/// Reads Win32 uninstall entries from HKLM (64-bit and 32-bit views) and HKCU.
/// </summary>
public sealed class RegistryAppSource : IInstalledAppSource
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly Regex KbPattern = new(@"^KB\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> SkippedReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update",
        "Hotfix",
        "Security Update",
    };

    public IReadOnlyList<InstalledApp> GetInstalledApps()
    {
        var appPaths = LazyLoad(() => AppPathsLocator.Load());
        var shortcuts = LazyLoad(() => StartMenuShortcutLocator.Load());

        var apps = new List<InstalledApp>();
        Collect(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64", apps, appPaths, shortcuts);
        Collect(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32", apps, appPaths, shortcuts);
        Collect(RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU", apps, appPaths, shortcuts);
        return Dedupe(apps);
    }

    private static T? LazyLoad<T>(Func<T> load) where T : class
    {
        try
        {
            return load();
        }
        catch
        {
            return null;
        }
    }

    private static void Collect(
        RegistryHive hive,
        RegistryView view,
        string idPrefix,
        List<InstalledApp> apps,
        AppPathsLocator? appPaths,
        StartMenuShortcutLocator? shortcuts)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath);
            if (uninstall is null)
                return;

            foreach (var subName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(subName);
                    var app = ReadEntry(key, idPrefix, subName, appPaths, shortcuts);
                    if (app is not null)
                        apps.Add(app);
                }
                catch
                {
                    // Individual entries may be unreadable; skip them.
                }
            }
        }
        catch
        {
            // Hive/view may be unavailable; skip it.
        }
    }

    private static InstalledApp? ReadEntry(
        RegistryKey? key,
        string idPrefix,
        string subName,
        AppPathsLocator? appPaths,
        StartMenuShortcutLocator? shortcuts)
    {
        if (key is null)
            return null;

        var displayName = key.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        if (key.GetValue("SystemComponent") is int systemComponent && systemComponent == 1)
            return null;

        if (key.GetValue("ParentKeyName") is string parentKey && !string.IsNullOrWhiteSpace(parentKey))
            return null;

        if (key.GetValue("ReleaseType") is string releaseType && SkippedReleaseTypes.Contains(releaseType))
            return null;

        displayName = displayName.Trim();
        if (KbPattern.IsMatch(displayName))
            return null;

        var installLocation = RegistryValueParser.NormalizeInstallLocation(key.GetValue("InstallLocation") as string);
        var iconPath = RegistryValueParser.ParseIconPath(key.GetValue("DisplayIcon") as string);
        var uninstallString = key.GetValue("UninstallString") as string;

        if (iconPath is not null && !File.Exists(iconPath))
            iconPath = null;

        // Install-location fallbacks, in order; each result is re-sanitized.
        installLocation ??= Sanitize(RegistryValueParser.DeriveInstallLocationFromIcon(iconPath));
        installLocation ??= Sanitize(RegistryValueParser.DeriveInstallLocationFromUninstallString(uninstallString));
        installLocation ??= Sanitize(appPaths?.FindInstallDir(displayName));
        installLocation ??= Sanitize(shortcuts?.FindInstallDir(displayName));

        return new InstalledApp
        {
            Id = $"reg:{idPrefix}:{subName}",
            Name = displayName,
            Publisher = (key.GetValue("Publisher") as string)?.Trim(),
            Version = (key.GetValue("DisplayVersion") as string)?.Trim(),
            InstallLocation = installLocation,
            IconPath = iconPath,
            EstimatedSizeBytes = ReadEstimatedSize(key),
            Source = AppSource.Registry,
            IsSystem = false,
        };
    }

    private static string? Sanitize(string? path)
        => RegistryValueParser.NormalizeInstallLocation(path);

    private static long? ReadEstimatedSize(RegistryKey key)
    {
        var value = key.GetValue("EstimatedSize");
        long kb = value switch
        {
            int i => i,
            long l => l,
            _ => 0,
        };
        return kb > 0 ? kb * 1024L : null;
    }

    private static List<InstalledApp> Dedupe(List<InstalledApp> apps)
    {
        var result = new List<InstalledApp>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in apps)
        {
            var key = NormalizeKey(app.Name) + "|" + NormalizeKey(app.Publisher);
            if (!seen.TryGetValue(key, out var existingIndex))
            {
                seen[key] = result.Count;
                result.Add(app);
                continue;
            }

            var existing = result[existingIndex];
            if (existing.InstallLocation is null && app.InstallLocation is not null)
                result[existingIndex] = app;
        }

        return result;
    }

    private static string NormalizeKey(string? s) => s?.Trim().ToLowerInvariant() ?? string.Empty;
}

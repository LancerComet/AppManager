using AppManager.Core.Inventory;
using AppManager.Core.Models;

namespace AppManager.Core.Scanning;

/// <summary>
/// Groups attributed folders into per-app usage groups, applying the
/// EstimatedSize fallback and computing the per-drive footprint of each group.
/// </summary>
public static class GroupBuilder
{
    public static List<AppUsageGroup> Build(
        IReadOnlyList<AttributedFolder> attributed,
        IReadOnlyList<InstalledApp> apps,
        string systemDriveRoot)
    {
        var systemRoot = systemDriveRoot.ToUpperInvariant();

        static string? DriveRootOf(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            try
            {
                return Path.GetPathRoot(path)?.ToUpperInvariant();
            }
            catch
            {
                return null;
            }
        }

        var groups = new List<AppUsageGroup>();

        foreach (var set in attributed
            .Where(a => a.Confidence != MatchConfidence.None)
            .GroupBy(a => a.App?.Id ?? "tool:" + a.Rule!.Id))
        {
            var first = set.First();
            var app = first.App;
            var tool = first.Rule;

            var group = new AppUsageGroup
            {
                Key = set.Key,
                DisplayName = app?.Name ?? tool!.DisplayName,
                Publisher = app?.Publisher,
                App = app,
                Tool = app is null ? tool : null,
                IconPath = app?.IconPath,
            };

            group.Folders.AddRange(set.OrderByDescending(f => f.Folder.SizeBytes));
            foreach (var f in set)
            {
                if (f.Folder.Category is FolderCategory.InstallDir or FolderCategory.LocalPrograms)
                    group.InstallBytes += f.Folder.SizeBytes;
                else
                    group.DataBytes += f.Folder.SizeBytes;
                if (DriveRootOf(f.Folder.FullPath) is { } drive)
                    group.BytesByDrive[drive] =
                        group.BytesByDrive.GetValueOrDefault(drive) + f.Folder.SizeBytes;
            }

            ApplyEstimatedSize(group, app, DriveRootOf, systemRoot);
            group.TotalBytes = group.InstallBytes + group.DataBytes;
            groups.Add(group);
        }

        // Registry apps with a known EstimatedSize but no attributed folders still
        // show up as groups so every installed app with known size is listed.
        var groupedAppIds = new HashSet<string>(
            groups.Where(g => g.App is not null).Select(g => g.App!.Id),
            StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (app.Source != AppSource.Registry || app.EstimatedSizeBytes is null or <= 0)
                continue;
            if (groupedAppIds.Contains(app.Id))
                continue;

            var group = new AppUsageGroup
            {
                Key = app.Id,
                DisplayName = app.Name,
                Publisher = app.Publisher,
                App = app,
                IconPath = app.IconPath,
            };
            ApplyEstimatedSize(group, app, DriveRootOf, systemRoot);
            group.TotalBytes = group.InstallBytes + group.DataBytes;
            groups.Add(group);
        }

        groups.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        return groups;
    }

    private static void ApplyEstimatedSize(
        AppUsageGroup group,
        InstalledApp? app,
        Func<string?, string?> driveRootOf,
        string systemRoot)
    {
        if (group.InstallBytes == 0 && app?.EstimatedSizeBytes is > 0)
        {
            var estimated = app.EstimatedSizeBytes.Value;
            group.InstallBytes = estimated;
            group.EstimatedInstallBytes = estimated;
            group.InstallSizeIsEstimated = true;
            var drive = driveRootOf(app.InstallLocation) ?? systemRoot;
            group.BytesByDrive[drive] =
                group.BytesByDrive.GetValueOrDefault(drive) + estimated;
        }
    }
}

using AppManager.Core.Inventory;
using AppManager.Core.Matching;
using AppManager.Core.Models;

namespace AppManager.Core.Scanning;

/// <summary>
/// Runs a full scan: app inventory, folder discovery, parallel size measurement
/// and app attribution. Always executes on background threads.
/// </summary>
public sealed class DiskScanner
{
    private readonly InstalledAppInventory _inventory;
    private readonly IReadOnlyList<KnownTool> _rules;

    public DiskScanner(InstalledAppInventory? inventory = null, IReadOnlyList<KnownTool>? rules = null)
    {
        _inventory = inventory ?? new InstalledAppInventory();
        _rules = rules ?? KnownTools.Load();
    }

    public Task<ScanResult> ScanAsync(
        ScanOptions options,
        ScanProgressState? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ScanInternalAsync(options, progress, cancellationToken), cancellationToken);
    }

    private Task<ScanResult> ScanInternalAsync(
        ScanOptions options,
        ScanProgressState? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        progress?.SetPhase(ScanPhase.EnumeratingApps);
        var apps = _inventory.GetInstalledApps();

        cancellationToken.ThrowIfCancellationRequested();

        progress?.SetPhase(ScanPhase.DiscoveringFolders);
        var candidates = ScanLocationProvider.Discover(apps, options);

        cancellationToken.ThrowIfCancellationRequested();

        progress?.SetTotal(candidates.Count);
        progress?.SetPhase(ScanPhase.Measuring);
        var usageList = FolderSizeCalculator.MeasureMany(
            candidates,
            Environment.ProcessorCount,
            cancellationToken,
            onFolderCompleted: (candidate, _) => progress?.IncrementDone(candidate.Path),
            onBytesDelta: d => progress?.AddBytes(d));

        progress?.SetPhase(ScanPhase.Matching);
        var matcher = new AppMatcher(apps, _rules);
        var attributed = matcher.Match(usageList);

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
        var groups = GroupBuilder.Build(attributed, apps, systemRoot);
        var unattributed = attributed
            .Where(a => a.Confidence == MatchConfidence.None)
            .Select(a => a.Folder)
            .Where(f => f.SizeBytes > 0 || f.FileCount > 0)
            .OrderByDescending(f => f.SizeBytes)
            .ToList();

        var (driveTotal, driveFree) = SystemDriveInfo();

        watch.Stop();
        progress?.SetPhase(ScanPhase.Done);

        return Task.FromResult(new ScanResult
        {
            Groups = groups,
            Unattributed = unattributed,
            InstalledApps = apps,
            DriveTotalBytes = driveTotal,
            DriveFreeBytes = driveFree,
            ScanDuration = watch.Elapsed,
            StartedAtUtc = startedAt,
        });
    }

    private static (long total, long free) SystemDriveInfo()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (root is not null)
            {
                var drive = new DriveInfo(root);
                return (drive.TotalSize, drive.AvailableFreeSpace);
            }
        }
        catch
        {
        }
        return (0, 0);
    }
}

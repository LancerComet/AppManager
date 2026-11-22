using System.Collections.Concurrent;
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
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ScanInternalAsync(options, progress, cancellationToken), cancellationToken);
    }

    private async Task<ScanResult> ScanInternalAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Report(progress, ScanPhase.EnumeratingApps, null, 0, 0, 0);
        var apps = _inventory.GetInstalledApps();

        cancellationToken.ThrowIfCancellationRequested();

        Report(progress, ScanPhase.DiscoveringFolders, null, 0, 0, 0);
        var candidates = ScanLocationProvider.Discover(apps, options);

        cancellationToken.ThrowIfCancellationRequested();

        var total = candidates.Count;
        var done = 0;
        var bytesSoFar = 0L;
        var usages = new ConcurrentBag<FolderUsage>();

        Report(progress, ScanPhase.Measuring, null, 0, total, 0);
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken,
            },
            (candidate, ct) =>
            {
                var lastReported = 0L;
                var usage = FolderSizeCalculator.Measure(candidate.Path, candidate.Category, ct, bytes =>
                {
                    var delta = bytes - Interlocked.Exchange(ref lastReported, bytes);
                    Interlocked.Add(ref bytesSoFar, delta);
                });
                usages.Add(usage);
                var finished = Interlocked.Increment(ref done);
                Report(progress, ScanPhase.Measuring, candidate.Path, finished, total, Interlocked.Read(ref bytesSoFar));
                return ValueTask.CompletedTask;
            });

        Report(progress, ScanPhase.Matching, null, total, total, Interlocked.Read(ref bytesSoFar));
        var usageList = usages.ToList();
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
        Report(progress, ScanPhase.Done, null, total, total, Interlocked.Read(ref bytesSoFar));

        return new ScanResult
        {
            Groups = groups,
            Unattributed = unattributed,
            InstalledApps = apps,
            DriveTotalBytes = driveTotal,
            DriveFreeBytes = driveFree,
            ScanDuration = watch.Elapsed,
            StartedAtUtc = startedAt,
        };
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

    private static void Report(
        IProgress<ScanProgress>? progress,
        ScanPhase phase,
        string? path,
        int done,
        int total,
        long bytes)
    {
        progress?.Report(new ScanProgress
        {
            Phase = phase,
            CurrentPath = path,
            FoldersDone = done,
            FoldersTotal = total,
            BytesSoFar = bytes,
        });
    }
}

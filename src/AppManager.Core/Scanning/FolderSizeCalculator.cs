using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.ExceptionServices;
using AppManager.Core.Models;

namespace AppManager.Core.Scanning;

/// <summary>
/// Measures folder trees' on-disk size using low-allocation file enumeration.
/// Reparse points (junctions/symlinks) are never recursed into, and OneDrive-style
/// cloud placeholder files count as 0 bytes (but still count as files).
/// Trees are measured by a shared directory-level work queue so one huge root
/// cannot pin a single worker while the others go idle.
/// </summary>
public static class FolderSizeCalculator
{
    private const long ReportIntervalBytes = 4 * 1024 * 1024;

    // Cloud placeholders: Offline (0x1000), RecallOnOpen (0x40000), RecallOnDataAccess (0x400000).
    private const FileAttributes CloudPlaceholderAttributes =
        FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    private static readonly EnumerationOptions EnumOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
    };

    private readonly struct FileInfoEntry
    {
        public readonly string? Path;
        public readonly bool IsDirectory;
        public readonly long Length;
        public readonly FileAttributes Attributes;
        public readonly DateTime LastWriteTimeUtc;

        public FileInfoEntry(string? path, bool isDirectory, long length,
            FileAttributes attributes, DateTime lastWriteTimeUtc)
        {
            Path = path;
            IsDirectory = isDirectory;
            Length = length;
            Attributes = attributes;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }
    }

    private sealed class MeasureEnumerator : FileSystemEnumerator<FileInfoEntry>
    {
        public int ErrorCount;

        public MeasureEnumerator(string directory)
            : base(directory, EnumOptions)
        {
        }

        protected override FileInfoEntry TransformEntry(ref FileSystemEntry entry)
            => new(entry.IsDirectory ? entry.ToFullPath() : null, entry.IsDirectory, entry.Length,
                entry.Attributes, entry.LastWriteTimeUtc.UtcDateTime);

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry) => true;

        protected override bool ContinueOnError(int error)
        {
            ErrorCount++;
            return true;
        }
    }

    private sealed class EnumerableAdapter : IEnumerable<FileInfoEntry>
    {
        private readonly MeasureEnumerator _enumerator;

        public EnumerableAdapter(MeasureEnumerator enumerator) => _enumerator = enumerator;

        public IEnumerator<FileInfoEntry> GetEnumerator() => _enumerator;

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _enumerator;
    }

    /// <summary>
    /// Measures <paramref name="path"/>. <paramref name="onBytes"/> is invoked
    /// periodically with the cumulative bytes counted so far in this folder.
    /// </summary>
    public static FolderUsage Measure(
        string path,
        FolderCategory category,
        CancellationToken cancellationToken,
        Action<long>? onBytes = null)
    {
        long cumulative = 0;
        long lastReport = 0;

        var roots = new[]
        {
            new FolderCandidate { Path = path, Category = category },
        };

        var result = MeasureMany(roots, 1, cancellationToken, onBytesDelta: d =>
        {
            if (onBytes is null)
                return;
            cumulative += d;
            if (cumulative - lastReport >= ReportIntervalBytes)
            {
                lastReport = cumulative;
                onBytes(cumulative);
            }
        });

        if (onBytes is not null && cumulative != lastReport)
            onBytes(cumulative);

        return result[0];
    }

    private sealed class RootState
    {
        public required FolderCandidate Candidate;
        public int Index;
        public long Size;
        public long Files;
        public long NewestTicks = DateTime.MinValue.Ticks;
        public int Inaccessible;
        public int Pending;
    }

    private readonly struct WorkItem
    {
        public readonly string Directory;
        public readonly RootState Root;

        public WorkItem(string directory, RootState root)
        {
            Directory = directory;
            Root = root;
        }
    }

    /// <summary>
    /// Measures every candidate root with one shared, LIFO directory work queue so
    /// that all workers can help on whichever tree is deepest at any moment.
    /// The result list is in the same order as <paramref name="roots"/>.
    /// </summary>
    /// <param name="onFolderCompleted">
    /// Invoked once per root when its tree is fully measured.
    /// </param>
    /// <param name="onBytesDelta">
    /// Invoked once per enumerated directory (bytes > 0) with that directory's
    /// file-size contribution; suitable for progress reporting.
    /// </param>
    public static IReadOnlyList<FolderUsage> MeasureMany(
        IReadOnlyList<FolderCandidate> roots,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken,
        Action<FolderCandidate, FolderUsage>? onFolderCompleted = null,
        Action<long>? onBytesDelta = null)
    {
        if (roots.Count == 0)
            return Array.Empty<FolderUsage>();

        var results = new FolderUsage[roots.Count];

        var rootStates = new RootState[roots.Count];
        var queue = new BlockingCollection<WorkItem>(new ConcurrentStack<WorkItem>());
        var outstanding = 0;
        Exception? firstException = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        for (var i = 0; i < roots.Count; i++)
        {
            var state = new RootState { Candidate = roots[i], Index = i };
            rootStates[i] = state;
            state.Pending = 1;
            outstanding++;
            queue.Add(new WorkItem(roots[i].Path, state));
        }

        var degree = Math.Max(1, maxDegreeOfParallelism);
        var workers = new Task[degree];
        for (var i = 0; i < degree; i++)
        {
            workers[i] = Task.Factory.StartNew(
                () => Consume(queue, linked, results, ref outstanding, ref firstException,
                    onFolderCompleted, onBytesDelta),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        Task.WaitAll(workers);

        if (firstException is not null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
        cancellationToken.ThrowIfCancellationRequested();

        return results;
    }

    private static void Consume(
        BlockingCollection<WorkItem> queue,
        CancellationTokenSource linked,
        FolderUsage[] results,
        ref int outstanding,
        ref Exception? firstException,
        Action<FolderCandidate, FolderUsage>? onFolderCompleted,
        Action<long>? onBytesDelta)
    {
        // Dedicated LongRunning threads, so no restore needed.
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        try
        {
            foreach (var item in queue.GetConsumingEnumerable(linked.Token))
            {
                var subdirs = ProcessDirectory(item, linked.Token, out var dirBytes,
                    out var dirFiles, out var dirNewestTicks, out var dirErrors);

                var root = item.Root;
                Interlocked.Add(ref root.Size, dirBytes);
                Interlocked.Add(ref root.Files, dirFiles);
                Interlocked.Add(ref root.Inaccessible, dirErrors);
                if (dirNewestTicks > 0)
                    MaxTicks(ref root.NewestTicks, dirNewestTicks);
                if (dirBytes > 0)
                    onBytesDelta?.Invoke(dirBytes);

                if (subdirs.Count > 0)
                {
                    Interlocked.Add(ref root.Pending, subdirs.Count);
                    Interlocked.Add(ref outstanding, subdirs.Count);
                    foreach (var dir in subdirs)
                        queue.Add(new WorkItem(dir, root));
                }

                if (Interlocked.Decrement(ref root.Pending) == 0)
                {
                    var usage = BuildUsage(root);
                    results[root.Index] = usage;
                    onFolderCompleted?.Invoke(root.Candidate, usage);
                }

                if (Interlocked.Decrement(ref outstanding) == 0)
                    queue.CompleteAdding();
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref firstException, ex, null);
            linked.Cancel();
        }
    }

    private static List<string> ProcessDirectory(
        WorkItem item,
        CancellationToken cancellationToken,
        out long dirBytes,
        out long dirFiles,
        out long dirNewestTicks,
        out int dirErrors)
    {
        dirBytes = 0;
        dirFiles = 0;
        dirNewestTicks = 0;
        dirErrors = 0;
        var subdirs = new List<string>();

        var enumerator = new MeasureEnumerator(item.Directory);
        try
        {
            foreach (var entry in new EnumerableAdapter(enumerator))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.IsDirectory)
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                        subdirs.Add(entry.Path!);
                    continue;
                }

                dirFiles++;
                if ((entry.Attributes & CloudPlaceholderAttributes) == 0)
                    dirBytes += entry.Length;
                if (entry.LastWriteTimeUtc.Ticks > dirNewestTicks)
                    dirNewestTicks = entry.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
            or System.Security.SecurityException)
        {
            dirErrors++;
        }

        dirErrors += enumerator.ErrorCount;
        return subdirs;
    }

    private static void MaxTicks(ref long field, long value)
    {
        var current = Interlocked.Read(ref field);
        while (value > current)
        {
            var prev = Interlocked.CompareExchange(ref field, value, current);
            if (prev == current)
                break;
            current = prev;
        }
    }

    private static FolderUsage BuildUsage(RootState root)
    {
        var path = root.Candidate.Path;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var newestTicks = Interlocked.Read(ref root.NewestTicks);
        return new FolderUsage
        {
            FullPath = trimmed,
            Name = Path.GetFileName(trimmed) ?? trimmed,
            Category = root.Candidate.Category,
            SizeBytes = Interlocked.Read(ref root.Size),
            FileCount = Interlocked.Read(ref root.Files),
            LastWriteTimeUtc = new DateTime(newestTicks, DateTimeKind.Utc),
            InaccessibleCount = Volatile.Read(ref root.Inaccessible),
        };
    }
}

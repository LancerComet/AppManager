using System.IO.Enumeration;
using AppManager.Core.Models;

namespace AppManager.Core.Scanning;

/// <summary>
/// Measures a folder tree's on-disk size using low-allocation file enumeration.
/// Reparse points (junctions/symlinks) are never recursed into, and OneDrive-style
/// cloud placeholder files count as 0 bytes (but still count as files).
/// </summary>
public static class FolderSizeCalculator
{
    private const long ReportIntervalBytes = 4 * 1024 * 1024;

    // Cloud placeholders: Offline (0x1000), RecallOnOpen (0x40000), RecallOnDataAccess (0x400000).
    private const FileAttributes CloudPlaceholderAttributes =
        FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    private readonly struct FileInfoEntry
    {
        public readonly long Length;
        public readonly FileAttributes Attributes;
        public readonly DateTime LastWriteTimeUtc;

        public FileInfoEntry(long length, FileAttributes attributes, DateTime lastWriteTimeUtc)
        {
            Length = length;
            Attributes = attributes;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }
    }

    private sealed class MeasureEnumerator : FileSystemEnumerator<FileInfoEntry>
    {
        public int ErrorCount;

        public MeasureEnumerator(string directory, EnumerationOptions options)
            : base(directory, options)
        {
        }

        protected override FileInfoEntry TransformEntry(ref FileSystemEntry entry)
            => new(entry.Length, entry.Attributes, entry.LastWriteTimeUtc.UtcDateTime);

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
            => !entry.IsDirectory;

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
            => entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0;

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
    /// periodically with the bytes counted so far in this folder.
    /// </summary>
    public static FolderUsage Measure(
        string path,
        FolderCategory category,
        CancellationToken cancellationToken,
        Action<long>? onBytes = null)
    {
        long size = 0;
        long files = 0;
        var newest = DateTime.MinValue;
        var inaccessible = 0;
        long lastReport = 0;

        var enumerator = new MeasureEnumerator(path, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
        });

        try
        {
            foreach (var entry in new EnumerableAdapter(enumerator))
            {
                cancellationToken.ThrowIfCancellationRequested();

                files++;
                if ((entry.Attributes & CloudPlaceholderAttributes) == 0)
                    size += entry.Length;
                if (entry.LastWriteTimeUtc > newest)
                    newest = entry.LastWriteTimeUtc;

                if (size - lastReport >= ReportIntervalBytes)
                {
                    lastReport = size;
                    onBytes?.Invoke(size);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
            or System.Security.SecurityException)
        {
            inaccessible++;
        }

        inaccessible += enumerator.ErrorCount;
        if (size != lastReport)
            onBytes?.Invoke(size);

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new FolderUsage
        {
            FullPath = trimmed,
            Name = Path.GetFileName(trimmed) ?? trimmed,
            Category = category,
            SizeBytes = size,
            FileCount = files,
            LastWriteTimeUtc = newest,
            InaccessibleCount = inaccessible,
        };
    }
}

using AppManager.Core.Models;

namespace AppManager.Core.Scanning;

/// <summary>
/// Thread-safe progress state written by <see cref="DiskScanner"/> and polled
/// by consumers (e.g. a UI timer). No callbacks are marshaled anywhere.
/// </summary>
public sealed class ScanProgressState
{
    private int _phase;
    private int _foldersDone;
    private int _foldersTotal;
    private long _bytesSoFar;
    private string? _currentPath;

    public ScanPhase Phase => (ScanPhase)Volatile.Read(ref _phase);
    public int FoldersDone => Volatile.Read(ref _foldersDone);
    public int FoldersTotal => Volatile.Read(ref _foldersTotal);
    public long BytesSoFar => Interlocked.Read(ref _bytesSoFar);
    public string? CurrentPath => Volatile.Read(ref _currentPath);

    /// <summary>Point-in-time copy of all progress values.</summary>
    public ScanProgress Snapshot() => new()
    {
        Phase = Phase,
        CurrentPath = CurrentPath,
        FoldersDone = FoldersDone,
        FoldersTotal = FoldersTotal,
        BytesSoFar = BytesSoFar,
    };

    internal void SetPhase(ScanPhase phase) => Volatile.Write(ref _phase, (int)phase);

    internal void SetTotal(int total) => Volatile.Write(ref _foldersTotal, total);

    internal void IncrementDone(string? path)
    {
        Volatile.Write(ref _currentPath, path);
        Interlocked.Increment(ref _foldersDone);
    }

    internal void AddBytes(long delta) => Interlocked.Add(ref _bytesSoFar, delta);
}

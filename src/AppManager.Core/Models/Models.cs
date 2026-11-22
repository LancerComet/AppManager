using System.Text.Json.Serialization;
using AppManager.Core.Matching;

namespace AppManager.Core.Models;

public enum AppSource
{
    Registry,
    Msix,
}

public sealed record InstalledApp
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public string? InstallLocation { get; init; }
    public string? IconPath { get; init; }
    public long? EstimatedSizeBytes { get; init; }
    public AppSource Source { get; init; }
    public string? PackageFamilyName { get; init; }
    public bool IsSystem { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<FolderCategory>))]
public enum FolderCategory
{
    UserHomeDot,
    UserHome,
    Documents,
    SavedGames,
    RoamingAppData,
    LocalAppData,
    LocalLowAppData,
    ProgramData,
    PackageData,
    LocalPrograms,
    InstallDir,
    Custom,
}

public sealed record FolderUsage
{
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public FolderCategory Category { get; init; }
    public long SizeBytes { get; init; }
    public long FileCount { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
    public int InaccessibleCount { get; init; }
}

public enum MatchConfidence
{
    None,
    Low,
    Medium,
    High,
}

/// <summary>Why a folder was attributed — language-neutral; the UI translates it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MatchReason>))]
public enum MatchReason
{
    None,
    /// <summary>MSIX package data dir named like a PackageFamilyName.</summary>
    PackageFamilyName,
    /// <summary>Folder equals or contains the app's install location.</summary>
    InstallDir,
    /// <summary>Matched a known-tool rule (ReasonArg = folder name).</summary>
    KnownRule,
    /// <summary>Name equals the app's install-dir leaf segment.</summary>
    InstallDirName,
    /// <summary>Name equals the app's display name.</summary>
    AppName,
    /// <summary>Name equals the app's executable/icon file name.</summary>
    ExeName,
    /// <summary>Folder named like the app's publisher.</summary>
    Publisher,
    /// <summary>Low-confidence prefix similarity.</summary>
    NameSimilar,
}

public sealed record AttributedFolder
{
    public required FolderUsage Folder { get; init; }
    public InstalledApp? App { get; init; }
    public KnownTool? Rule { get; init; }
    public MatchConfidence Confidence { get; init; }
    public MatchReason Reason { get; init; }
    public string? ReasonArg { get; init; }
}

public sealed class AppUsageGroup
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public InstalledApp? App { get; init; }
    public KnownTool? Tool { get; init; }
    public string? IconPath { get; init; }
    public List<AttributedFolder> Folders { get; set; } = new();
    public long TotalBytes { get; set; }
    public long InstallBytes { get; set; }
    public long DataBytes { get; set; }
    /// <summary>Footprint per drive root ("C:\", "D:\" — upper-case), including estimated install bytes.</summary>
    public Dictionary<string, long> BytesByDrive { get; set; } = new();
    public long? EstimatedInstallBytes { get; set; }
    public bool InstallSizeIsEstimated { get; set; }
}

public sealed record ScanResult
{
    public required IReadOnlyList<AppUsageGroup> Groups { get; init; }
    public required IReadOnlyList<FolderUsage> Unattributed { get; init; }
    public required IReadOnlyList<InstalledApp> InstalledApps { get; init; }
    public long DriveTotalBytes { get; init; }
    public long DriveFreeBytes { get; init; }
    public TimeSpan ScanDuration { get; init; }
    public DateTime StartedAtUtc { get; init; }
}

public enum ScanPhase
{
    EnumeratingApps,
    DiscoveringFolders,
    Measuring,
    Matching,
    Done,
}

public sealed record ScanProgress
{
    public ScanPhase Phase { get; init; }
    public string? CurrentPath { get; init; }
    public int FoldersDone { get; init; }
    public int FoldersTotal { get; init; }
    public long BytesSoFar { get; init; }
}

public sealed record ScanOptions
{
    public IReadOnlyList<string> ExtraRoots { get; init; } = Array.Empty<string>();
}

/// <summary>Data-type classification for harness (LLM tool) state.</summary>
public enum HarnessKind
{
    Conversations,
    Artifacts,
    Snapshots,
    Cache,
    Logs,
    Extensions,
    State,
    OldVersions,
    Program,
    Versions,
    VersionSet,
}

public enum HarnessType
{
    Cli,
    Ide,
    Desktop,
}

/// <summary>One measured file or folder belonging to a harness (non-program data).</summary>
public sealed class HarnessEntry
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public HarnessKind Kind { get; set; }
    public long SizeBytes { get; set; }
    public long FileCount { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public bool IsFile { get; set; }
}

/// <summary>Per-harness totals and classified entries.</summary>
public sealed class HarnessUsage
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public HarnessType Type { get; set; }
    public string? IconAppPath { get; set; }
    public long TotalBytes { get; set; }
    public long ExcludedProgramBytes { get; set; }
    public Dictionary<HarnessKind, long> BytesByKind { get; set; } = new();
    public List<HarnessEntry> Entries { get; set; } = new();
}

public sealed class HarnessScanResult
{
    public List<HarnessUsage> Harnesses { get; set; } = new();
    public DateTime ScannedAtUtc { get; set; }
    public TimeSpan Duration { get; set; }
}

using System.Diagnostics;
using AppManager.Core.Models;
using AppManager.Core.Scanning;
using AppManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;

namespace AppManager.ViewModels;

public static class DriveHelpers
{
    public static string SystemRoot { get; } =
        Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

    public static bool IsOnSystemDrive(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        try
        {
            var root = Path.GetPathRoot(path);
            return root is not null && root.Equals(SystemRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string ConfidenceText(MatchConfidence c) => c switch
    {
        MatchConfidence.High or MatchConfidence.Medium or MatchConfidence.Low
            => AppServices.Loc.Get($"Confidence_{c}"),
        _ => "",
    };

    /// <summary>Localized match-reason text, formatted with ReasonArg.</summary>
    public static string ReasonText(AttributedFolder f)
        => f.ReasonArg is not null
            ? AppServices.Loc.Format($"MatchReason_{f.Reason}", f.ReasonArg)
            : AppServices.Loc.TryGet($"MatchReason_{f.Reason}") ?? string.Empty;

    /// <summary>KnownTool description: localized resource, else the JSON text.</summary>
    public static string? ToolDescription(AppManager.Core.Matching.KnownTool? tool)
        => tool is null
            ? null
            : AppServices.Loc.TryGet($"KnownTool_{tool.Id}_Description") ?? tool.Description;

    public static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch
        {
        }
    }
}

/// <summary>
/// An app/tool group as shown in list UIs, with filter-adjusted totals
/// (仅 C 盘 / 隐藏低置信度) and bar proportions.
/// </summary>
public partial class AppItemViewModel : ObservableObject
{
    internal AppUsageGroup Group { get; set; } = null!;
    internal string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? PublisherText { get; set; }
    public string IconLetter { get; set; } = "?";
    internal IReadOnlyList<AttributedFolder> ShownFolders { get; set; } = [];

    internal long Total { get; set; }
    internal long Install { get; set; }
    internal long Data { get; set; }

    public double BarStars { get; set; }     // item size relative to list max (0..100)
    public double RestStars { get; set; }
    public double InstallStars { get; set; } // install/data split inside the bar
    public double DataStars { get; set; }

    public bool IsEstimated => Group.InstallSizeIsEstimated;

    /// <summary>Per-drive breakdown for the detail pane: "C: 12.3 GB · D: 150.2 GB".</summary>
    public string DriveSplitText => string.Join(" · ", Group.BytesByDrive
        .Where(kv => kv.Value > 0)
        .OrderByDescending(kv => kv.Value)
        .Select(kv => $"{kv.Key.TrimEnd('\\')} {SizeFormatter.Format(kv.Value)}"));
    public bool HasDriveSplit => DriveSplitText.Length > 0;
    public bool IsTool => Group.App is null;
    public bool HasLowConfidence { get; set; }
    public int FolderCount => ShownFolders.Count;
    public string FolderCountText => AppServices.Loc.Format("Common_FolderCount", FolderCount);

    public string TotalText => SizeFormatter.Format(Total);

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    public void StartIconLoad() => _ = LoadIconAsync();

    private async Task LoadIconAsync() => Icon = await AppServices.Icons.GetIconAsync(Group.IconPath);

    public static AppItemViewModel Create(
        AppUsageGroup group,
        bool hideLowConfidence,
        long maxTotal)
    {
        var shown = group.Folders
            .Where(f => !hideLowConfidence || f.Confidence != MatchConfidence.Low)
            .ToList();

        long install = 0, data = 0;
        foreach (var f in shown)
        {
            if (f.Folder.Category is FolderCategory.InstallDir or FolderCategory.LocalPrograms)
                install += f.Folder.SizeBytes;
            else
                data += f.Folder.SizeBytes;
        }

        if (group.InstallSizeIsEstimated && group.EstimatedInstallBytes is > 0)
            install += group.EstimatedInstallBytes.Value;

        var total = install + data;
        var barStars = maxTotal > 0 ? total * 100.0 / maxTotal : 0;
        var installShare = total > 0 ? install * 100.0 / total : 0;

        return new AppItemViewModel
        {
            Group = group,
            Key = group.Key,
            Name = group.DisplayName,
            PublisherText = group.Publisher,
            IconLetter = string.IsNullOrEmpty(group.DisplayName) ? "?" : group.DisplayName[..1],
            ShownFolders = shown,
            Total = total,
            Install = install,
            Data = data,
            BarStars = barStars,
            RestStars = Math.Max(0, 100 - barStars),
            InstallStars = installShare,
            DataStars = Math.Max(0, 100 - installShare),
            HasLowConfidence = shown.Any(f => f.Confidence == MatchConfidence.Low),
        };
    }
}

/// <summary>A folder row in the app detail pane.</summary>
public partial class FolderRowViewModel : ObservableObject
{
    internal AttributedFolder Folder { get; set; } = null!;

    public string Path => Folder.Folder.FullPath;
    public string CategoryLabel => CategoryNames.Of(Folder.Folder.Category);
    public string SizeText => SizeFormatter.Format(Folder.Folder.SizeBytes);
    public string Reason => DriveHelpers.ReasonText(Folder);
    public MatchConfidence Confidence => Folder.Confidence;
    public string ConfidenceText => DriveHelpers.ConfidenceText(Folder.Confidence);

    public string LastWriteText => Folder.Folder.LastWriteTimeUtc == DateTime.MinValue
        ? string.Empty
        : Folder.Folder.LastWriteTimeUtc.ToLocalTime().ToString("d", AppServices.Loc.Culture);
    public bool HasLastWrite => LastWriteText.Length > 0;

    public int InaccessibleCount => Folder.Folder.InaccessibleCount;
    public bool HasInaccessible => InaccessibleCount > 0;
    public string InaccessibleText => AppServices.Loc.Format("Common_Inaccessible", InaccessibleCount);

    public double SizeStars { get; set; }
    public double RestStars { get; set; }

    [RelayCommand]
    private void OpenInExplorer() => DriveHelpers.OpenInExplorer(Path);

    public static FolderRowViewModel Create(AttributedFolder folder, long maxSize)
        => new()
        {
            Folder = folder,
            SizeStars = maxSize > 0 ? folder.Folder.SizeBytes * 100.0 / maxSize : 0,
            RestStars = maxSize > 0 ? Math.Max(0, 100 - folder.Folder.SizeBytes * 100.0 / maxSize) : 100,
        };
}

/// <summary>An unattributed folder row.</summary>
public partial class UnattributedItemViewModel : ObservableObject
{
    internal FolderUsage Folder { get; set; } = null!;

    public string Name => Folder.Name;
    public string Path => Folder.FullPath;
    public string CategoryLabel => CategoryNames.Of(Folder.Category);
    internal FolderCategory Category => Folder.Category;
    public string SizeText => SizeFormatter.Format(Folder.SizeBytes);
    public string LastWriteText => Folder.LastWriteTimeUtc == DateTime.MinValue
        ? string.Empty
        : Folder.LastWriteTimeUtc.ToLocalTime().ToString("d", AppServices.Loc.Culture);
    public bool HasLastWrite => LastWriteText.Length > 0;

    public bool MaybeStale => Folder.LastWriteTimeUtc != DateTime.MinValue &&
        Folder.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-180);

    public int InaccessibleCount => Folder.InaccessibleCount;
    public bool HasInaccessible => InaccessibleCount > 0;
    public string InaccessibleText => AppServices.Loc.Format("Common_Inaccessible", InaccessibleCount);

    public double SizeStars { get; set; }
    public double RestStars { get; set; }

    [RelayCommand]
    private void OpenInExplorer() => DriveHelpers.OpenInExplorer(Path);

    public static UnattributedItemViewModel Create(FolderUsage folder, long maxSize)
        => new()
        {
            Folder = folder,
            SizeStars = maxSize > 0 ? folder.SizeBytes * 100.0 / maxSize : 0,
            RestStars = maxSize > 0 ? Math.Max(0, 100 - folder.SizeBytes * 100.0 / maxSize) : 100,
        };
}

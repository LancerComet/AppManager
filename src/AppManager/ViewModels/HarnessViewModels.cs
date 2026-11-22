using System.Diagnostics;
using AppManager.Core.Models;
using AppManager.Core.Scanning;
using AppManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;

namespace AppManager.ViewModels;

/// <summary>A harness row in the AI-tools master list.</summary>
public partial class HarnessItemViewModel : ObservableObject
{
    internal HarnessUsage Usage { get; set; } = null!;

    public string Key => Usage.Id;
    /// <summary>Localized harness name if a resource exists, else catalog name.</summary>
    public string Name => AppServices.Loc.TryGet($"Harness_{Usage.Id}_Name") ?? Usage.Name;

    public string Subtitle => $"{Usage.Publisher} · {TypeLabel}";
    public string TypeLabel => Usage.Type switch
    {
        HarnessType.Ide => AppServices.Loc.Get("Harness_Type_Ide"),
        HarnessType.Desktop => AppServices.Loc.Get("Harness_Type_Desktop"),
        _ => AppServices.Loc.Get("Harness_Type_Cli"),
    };
    public string TypeGlyph => Usage.Type switch
    {
        HarnessType.Ide => "\uE943",
        HarnessType.Desktop => "\uE8BD",
        _ => "\uE756",
    };

    public string TotalText => SizeFormatter.Format(Usage.TotalBytes);
    public double BarStars { get; set; }
    public double RestStars { get; set; }

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    public void StartIconLoad() => _ = LoadIconAsync();

    private async Task LoadIconAsync()
        => Icon = await AppServices.Icons.GetIconAsync(Usage.IconAppPath);

    public static HarnessItemViewModel Create(HarnessUsage usage, long maxTotal)
        => new()
        {
            Usage = usage,
            BarStars = maxTotal > 0 ? usage.TotalBytes * 100.0 / maxTotal : 0,
            RestStars = maxTotal > 0 ? Math.Max(0, 100 - usage.TotalBytes * 100.0 / maxTotal) : 100,
        };
}

/// <summary>A kind-total row in the detail pane's per-kind section.</summary>
public partial class HarnessKindRowViewModel : ObservableObject
{
    public HarnessKind Kind { get; set; }
    public string Label => HarnessKindNames.Of(Kind);
    public string SizeText { get; set; } = string.Empty;
    public double BarStars { get; set; }
    public double RestStars { get; set; }
}

/// <summary>A folder/file group header (one kind) with its entries.</summary>
public partial class HarnessKindGroupViewModel : ObservableObject
{
    public HarnessKind Kind { get; set; }
    public string HeaderText { get; set; } = string.Empty;
    public List<HarnessEntryViewModel> Entries { get; set; } = new();
}

/// <summary>One measured file/dir row inside a kind group.</summary>
public partial class HarnessEntryViewModel : ObservableObject
{
    internal HarnessEntry Entry { get; set; } = null!;

    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public string SizeText => SizeFormatter.Format(Entry.SizeBytes);
    public string LastWriteText => Entry.LastWriteTimeUtc == DateTime.MinValue
        ? string.Empty
        : Entry.LastWriteTimeUtc.ToLocalTime().ToString("d", AppServices.Loc.Culture);
    public bool HasLastWrite => LastWriteText.Length > 0;
    public string FileCountText => Entry.IsFile
        ? AppServices.Loc.Get("Common_File")
        : AppServices.Loc.Format("Common_FileCount", Entry.FileCount);

    public double SizeStars { get; set; }
    public double RestStars { get; set; }

    [RelayCommand]
    private void OpenInExplorer()
    {
        try
        {
            if (Entry.IsFile)
                Process.Start("explorer.exe", $"/select,\"{Entry.Path}\"");
            else
                DriveHelpers.OpenInExplorer(Entry.Path);
        }
        catch
        {
        }
    }

    public static HarnessEntryViewModel Create(HarnessEntry entry, long maxSize)
        => new()
        {
            Entry = entry,
            SizeStars = maxSize > 0 ? entry.SizeBytes * 100.0 / maxSize : 0,
            RestStars = maxSize > 0 ? Math.Max(0, 100 - entry.SizeBytes * 100.0 / maxSize) : 100,
        };
}

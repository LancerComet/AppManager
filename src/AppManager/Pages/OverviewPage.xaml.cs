using System.ComponentModel;
using AppManager.Core.Models;
using AppManager.Core.Scanning;
using AppManager.Services;
using AppManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AppManager.Pages;

/// <summary>A row in the overview category breakdown.</summary>
public sealed class CategoryRow
{
    public required string Label { get; init; }
    public required string SizeText { get; init; }
    public required double SizeStars { get; init; }
    public required double RestStars { get; init; }
}

/// <summary>A fixed local drive on the overview page.</summary>
public sealed class DriveRow
{
    public required string Label { get; init; }
    public required string Detail { get; init; }
    public required double UsedPercent { get; init; }
}

public sealed partial class OverviewPage : Page, IDetachable
{
    public ScanSession Session => AppServices.Session;

    private readonly PropertyChangedEventHandler _sessionHandler;

    public OverviewPage()
    {
        InitializeComponent();

        // ScrollViewer centers undersized content using DesiredSize, which
        // drifts when MaxWidth clamps during arrange — pin the panel width so
        // desired == arranged and margins stay symmetric.
        ContentScroller.SizeChanged += (_, e) =>
            ContentPanel.Width = Math.Max(0, Math.Min(ContentPanel.MaxWidth,
                e.NewSize.Width - ContentScroller.Padding.Left - ContentScroller.Padding.Right));

        _sessionHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(ScanSession.Result) or nameof(ScanSession.LastScanTime))
                Refresh();
            else if (e.PropertyName is nameof(ScanSession.FoldersDone)
                     or nameof(ScanSession.FoldersTotal)
                     or nameof(ScanSession.BytesSoFar)
                     or nameof(ScanSession.CurrentPath))
                RefreshProgress();
        };
        Session.PropertyChanged += _sessionHandler;
        Refresh();
    }

    public void Detach() => Session.PropertyChanged -= _sessionHandler;

    // Lightweight update for measuring-phase progress only — Refresh() callers
    // and progress property changes both funnel here.
    private void RefreshProgress()
    {
        if (!Session.IsScanning)
            return;
        var total = Session.FoldersTotal;
        ScanBar.IsIndeterminate = total <= 0;
        ScanBar.Maximum = Math.Max(1, total);
        ScanBar.Value = Math.Min(Session.FoldersDone, ScanBar.Maximum);
        ProgressDetail.Text = AppServices.Loc.Format("Overview_ProgressDetail",
            Session.FoldersDone, total, SizeFormatter.Format(Session.BytesSoFar));
        ProgressPath.Text = Session.CurrentPath ?? string.Empty;
    }

    private void Refresh()
    {
        var result = Session.Result;

        // All fixed local drives (independent of scan state).
        var driveRows = new List<DriveRow>();
        try
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                if (di.DriveType != DriveType.Fixed || !di.IsReady)
                    continue;
                var used = di.TotalSize - di.AvailableFreeSpace;
                var label = di.Name.TrimEnd('\\');
                if (di.RootDirectory.FullName.Equals(
                        DriveHelpers.SystemRoot, StringComparison.OrdinalIgnoreCase))
                    label += AppServices.Loc.Get("Overview_SystemDriveSuffix");
                driveRows.Add(new DriveRow
                {
                    Label = label,
                    Detail = AppServices.Loc.Format("Overview_DriveDetail",
                        SizeFormatter.Format(used),
                        SizeFormatter.Format(di.AvailableFreeSpace),
                        SizeFormatter.Format(di.TotalSize)),
                    UsedPercent = di.TotalSize > 0
                        ? Math.Clamp(used * 100.0 / di.TotalSize, 0, 100)
                        : 0,
                });
            }
        }
        catch
        {
        }
        DriveList.ItemsSource = driveRows;

        RefreshProgress();

        // Last scan info
        if (Session.LastScanTime is { } t && result is not null)
            LastScanText.Text = AppServices.Loc.Format("Overview_LastScan",
                t.ToString("g", AppServices.Loc.Culture),
                (int)Math.Round(result.ScanDuration.TotalSeconds));
        else
            LastScanText.Text = AppServices.Loc.Get("Overview_NeverScanned");

        if (result is null)
        {
            StatsGrid.Visibility = Visibility.Collapsed;
            CategoryArea.Visibility = Visibility.Collapsed;
            TopAppsArea.Visibility = Visibility.Collapsed;
            return;
        }

        StatsGrid.Visibility = Visibility.Visible;
        CategoryArea.Visibility = Visibility.Visible;
        TopAppsArea.Visibility = Visibility.Visible;

        long attributed = result.Groups.Sum(g => g.TotalBytes);
        var unattributed = result.Unattributed;
        var unBytes = unattributed.Sum(f => f.SizeBytes);
        var folderCount = result.Groups.Sum(g => g.Folders.Count) + unattributed.Count;

        StatAttributed.Text = SizeFormatter.Format(attributed);
        StatUnattributed.Text = SizeFormatter.Format(unBytes);
        StatApps.Text = result.InstalledApps.Count.ToString(AppServices.Loc.Culture);
        StatFolders.Text = folderCount.ToString(AppServices.Loc.Culture);

        // Category breakdown across attributed + unattributed folders.
        var byCat = new Dictionary<FolderCategory, long>();
        void Add(FolderUsage f)
        {
            byCat[f.Category] = byCat.GetValueOrDefault(f.Category) + f.SizeBytes;
        }
        foreach (var g in result.Groups)
            foreach (var f in g.Folders)
                Add(f.Folder);
        foreach (var f in unattributed)
            Add(f);

        var maxCat = byCat.Count > 0 ? byCat.Values.Max() : 0;
        CategoryList.ItemsSource = CategoryNames.All
            .Where(c => byCat.GetValueOrDefault(c) > 0)
            .Select(c => new CategoryRow
            {
                Label = CategoryNames.Of(c),
                SizeText = SizeFormatter.Format(byCat[c]),
                SizeStars = maxCat > 0 ? byCat[c] * 100.0 / maxCat : 0,
                RestStars = maxCat > 0 ? Math.Max(0, 100 - byCat[c] * 100.0 / maxCat) : 100,
            }).ToList();

        // Top 10 apps
        var hideLow = AppServices.Settings.HideLowConfidence;
        var items = result.Groups
            .Select(g => AppItemViewModel.Create(g, hideLow, 0))
            .Where(v => v.Total > 0)
            .OrderByDescending(v => v.Total)
            .Take(10)
            .ToList();
        var maxTop = items.Count > 0 ? items[0].Total : 0;
        foreach (var v in items)
        {
            v.BarStars = maxTop > 0 ? v.Total * 100.0 / maxTop : 0;
            v.RestStars = Math.Max(0, 100 - v.BarStars);
            v.StartIconLoad();
        }
        TopAppsList.ItemsSource = items;
    }

    private void TopApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppItemViewModel vm)
            AppServices.MainWindow?.NavigateToApp(vm.Key);
    }
}

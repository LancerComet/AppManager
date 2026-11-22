using System.Collections.ObjectModel;
using AppManager.Core.Models;
using AppManager.Core.Scanning;
using AppManager.Services;
using AppManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AppManager.Pages;

public sealed partial class AppsPage : Page
{
    public ScanSession Session => AppServices.Session;
    public SettingsService Settings => AppServices.Settings;
    public ObservableCollection<AppItemViewModel> Items { get; } = new();

    private string? _pendingSelectKey;
    private bool _selectFirst;

    public AppsPage()
    {
        InitializeComponent();

        // Pin detail content to the viewport — see OverviewPage for why.
        DetailPane.SizeChanged += (_, e) =>
            DetailStack.Width = Math.Max(0, e.NewSize.Width);

        Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScanSession.Result))
                Rebuild();
        };
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsService.HideLowConfidence))
                Rebuild();
        };
        Rebuild();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string key)
        {
            if (key == "auto-first")
                _selectFirst = true;
            else
                _pendingSelectKey = key;
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var result = Session.Result;
        var hideLow = Settings.HideLowConfidence;
        var search = SearchBox?.Text?.Trim() ?? string.Empty;
        var sourceIdx = SourceBox?.SelectedIndex ?? 0;

        Items.Clear();
        if (result is not null)
        {
            var groups = result.Groups.AsEnumerable();

            if (sourceIdx == 1)
                groups = groups.Where(g => g.App?.Source == AppSource.Registry);
            else if (sourceIdx == 2)
                groups = groups.Where(g => g.App?.Source == AppSource.Msix);
            else if (sourceIdx == 3)
                groups = groups.Where(g => g.App is null);

            if (search.Length > 0)
                groups = groups.Where(g =>
                    g.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (g.Publisher?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

            var vms = groups
                .Select(g => AppItemViewModel.Create(g, hideLow, 0))
                .Where(v => v.Total > 0 || v.ShownFolders.Count > 0)
                .ToList();

            var max = vms.Count > 0 ? vms.Max(v => v.Total) : 0;
            foreach (var v in vms)
            {
                v.BarStars = max > 0 ? v.Total * 100.0 / max : 0;
                v.RestStars = Math.Max(0, 100 - v.BarStars);
            }

            IEnumerable<AppItemViewModel> sorted = SortBox?.SelectedIndex switch
            {
                1 => vms.OrderByDescending(v => v.Data),
                2 => vms.OrderByDescending(v => v.Install),
                3 => vms.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => vms.OrderByDescending(v => v.Total),
            };

            foreach (var v in sorted)
            {
                v.StartIconLoad();
                Items.Add(v);
            }
        }

        // Apply pending selection — deferred so the ListView has realized the
        // new items (deterministic even when a rebuild just replaced them).
        if (_selectFirst && Items.Count > 0 && !Session.IsScanning)
        {
            _selectFirst = false;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Items.Count > 0)
                    ItemsList.SelectedItem = Items[0];
            });
        }
        else if (_pendingSelectKey is not null)
        {
            var found = Items.FirstOrDefault(i => i.Key == _pendingSelectKey);
            if (found is not null)
            {
                ItemsList.SelectedItem = found;
                _pendingSelectKey = null;
            }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Rebuild();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => Rebuild();

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var vm = ItemsList.SelectedItem as AppItemViewModel;
        if (vm is null)
        {
            DetailContent.Visibility = Visibility.Collapsed;
            DetailEmpty.Visibility = Visibility.Visible;
            return;
        }

        vm.StartIconLoad();
        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;

        DetailName.Text = vm.Name;
        DetailPublisher.Text = vm.PublisherText ?? string.Empty;
        var g = vm.Group;
        DetailVersion.Text = g.App is not null
            ? (g.App.Version is { } v ? $"版本 {v}" : string.Empty)
            : g.Tool?.Description ?? string.Empty;
        var installLoc = g.App?.InstallLocation;
        DetailInstall.Text = installLoc ?? string.Empty;
        DetailInstall.Visibility = installLoc is null ? Visibility.Collapsed : Visibility.Visible;
        OpenInstallButton.Visibility = installLoc is not null && Directory.Exists(installLoc)
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailTotal.Text = SizeFormatter.Format(vm.Total);
        DetailInstallBytes.Text = SizeFormatter.Format(vm.Install);
        DetailDataBytes.Text = SizeFormatter.Format(vm.Data);
        DetailDriveSplit.Text = vm.DriveSplitText;
        DetailDriveSplit.Visibility = vm.HasDriveSplit ? Visibility.Visible : Visibility.Collapsed;
        DetailEstimatedNote.Visibility = vm.IsEstimated ? Visibility.Visible : Visibility.Collapsed;
        DetailEstimatedNote.Text = "安装大小来自注册表估算值";

        DetailIcon.Source = null;
        DetailIconFallback.Visibility = Visibility.Visible;
        DetailIconLetter.Text = vm.IconLetter;
        _ = LoadDetailIconAsync(vm);

        var max = vm.ShownFolders.Count > 0 ? vm.ShownFolders.Max(f => f.Folder.SizeBytes) : 0;
        FolderList.ItemsSource = vm.ShownFolders
            .OrderByDescending(f => f.Folder.SizeBytes)
            .Select(f => FolderRowViewModel.Create(f, max))
            .ToList();
    }

    private async Task LoadDetailIconAsync(AppItemViewModel vm)
    {
        var icon = await AppServices.Icons.GetIconAsync(vm.Group.IconPath);
        if (ItemsList.SelectedItem == vm && icon is not null)
        {
            DetailIcon.Source = icon;
            DetailIconFallback.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenInstall_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is AppItemViewModel vm &&
            vm.Group.App?.InstallLocation is { } loc && Directory.Exists(loc))
            DriveHelpers.OpenInExplorer(loc);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var result = Session.Result;
        if (result is null)
            return;

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(AppServices.MainWindow!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = $"AppManager-报告-{DateTime.Now:yyyyMMdd-HHmm}";
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        var rows = Items
            .Select(i => new CsvReportBuilder.Row(i.Group, i.ShownFolders, i.Total, i.Install, i.Data))
            .ToList();
        var csv = CsvReportBuilder.Build(rows, CategoryNames.Of);
        await File.WriteAllTextAsync(file.Path, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}

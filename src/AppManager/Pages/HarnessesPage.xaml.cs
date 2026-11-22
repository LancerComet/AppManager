using System.Collections.ObjectModel;
using AppManager.Core.Models;
using AppManager.Core.Scanning;
using AppManager.Services;
using AppManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AppManager.Pages;

public sealed partial class HarnessesPage : Page
{
    public HarnessSession Session => AppServices.Harness;
    public ObservableCollection<HarnessItemViewModel> Items { get; } = new();

    private string? _pendingSelectKey;

    public HarnessesPage()
    {
        InitializeComponent();

        // Pin detail content to the viewport — see OverviewPage for why.
        DetailPane.SizeChanged += (_, e) =>
            DetailStack.Width = Math.Max(0, e.NewSize.Width - DetailPane.Padding.Right);

        SubtitleText.Text = $"仅统计当前用户（{Environment.UserName}）的数据，不含程序本体";

        Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HarnessSession.Result) or nameof(HarnessSession.IsScanning))
                Rebuild();
        };
        Rebuild();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string key)
            _pendingSelectKey = key;
        Session.EnsureScanned();
    }

    private void Rebuild()
    {
        // Items.Clear() drops the selection — remember which key was selected
        // (or is pending from a debug launch) and restore it afterwards.
        var selectKey = _pendingSelectKey
            ?? (ItemsList.SelectedItem as HarnessItemViewModel)?.Key;

        var result = Session.Result;
        Items.Clear();
        if (result is not null)
        {
            var max = result.Harnesses.Count > 0 ? result.Harnesses.Max(h => h.TotalBytes) : 0;
            foreach (var h in result.Harnesses)
            {
                var vm = HarnessItemViewModel.Create(h, max);
                vm.StartIconLoad();
                Items.Add(vm);
            }
        }
        EmptyText.Visibility = result is not null && Items.Count == 0 && !Session.IsScanning
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (selectKey is not null && Items.Count > 0)
        {
            var found = Items.FirstOrDefault(i => i.Key.Equals(selectKey,
                StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                ItemsList.SelectedItem = found;
                _pendingSelectKey = null;
            }
        }
        else if (ItemsList.SelectedItem is null && Items.Count > 0 && !Session.IsScanning)
        {
            ItemsList.SelectedItem = Items[0];
        }
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is not HarnessItemViewModel vm)
        {
            DetailContent.Visibility = Visibility.Collapsed;
            DetailEmpty.Visibility = Visibility.Visible;
            return;
        }

        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;

        var u = vm.Usage;
        DetailName.Text = u.Name;
        DetailPublisher.Text = vm.Subtitle;
        DetailTotal.Text = SizeFormatter.Format(u.TotalBytes);
        DetailExcluded.Text = u.ExcludedProgramBytes > 0
            ? $" · 未计入程序本体 {SizeFormatter.Format(u.ExcludedProgramBytes)}"
            : string.Empty;

        DetailIcon.Source = null;
        DetailIconFallback.Visibility = Visibility.Visible;
        DetailIconGlyph.Glyph = vm.TypeGlyph;
        _ = LoadDetailIconAsync(vm);

        KindList.ItemsSource = u.BytesByKind
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new HarnessKindRowViewModel
            {
                Kind = kv.Key,
                SizeText = SizeFormatter.Format(kv.Value),
                BarStars = u.TotalBytes > 0 ? kv.Value * 100.0 / u.TotalBytes : 0,
                RestStars = u.TotalBytes > 0 ? Math.Max(0, 100 - kv.Value * 100.0 / u.TotalBytes) : 100,
            })
            .ToList();

        var maxEntry = u.Entries.Count > 0 ? u.Entries.Max(x => x.SizeBytes) : 0;
        EntryGroups.ItemsSource = u.Entries
            .Where(x => x.SizeBytes > 0)
            .GroupBy(x => x.Kind)
            .OrderBy(g => g.Key)
            .Select(g => new HarnessKindGroupViewModel
            {
                Kind = g.Key,
                HeaderText = $"{HarnessKindNames.Of(g.Key)} · {SizeFormatter.Format(g.Sum(x => x.SizeBytes))}",
                Entries = g.OrderByDescending(x => x.SizeBytes)
                    .Select(x => HarnessEntryViewModel.Create(x, maxEntry))
                    .ToList(),
            })
            .ToList();
    }

    private async Task LoadDetailIconAsync(HarnessItemViewModel vm)
    {
        var icon = await AppServices.Icons.GetIconAsync(vm.Usage.IconAppPath);
        if (ItemsList.SelectedItem == vm && icon is not null)
        {
            DetailIcon.Source = icon;
            DetailIconFallback.Visibility = Visibility.Collapsed;
        }
    }

    // A BitmapImage whose file is gone/corrupt fails silently and stays non-null,
    // which would hide the type-glyph fallback — clear it so the fallback shows.
    private void ItemIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if ((sender as Image)?.DataContext is HarnessItemViewModel vm)
            vm.Icon = null;
    }

    private void DetailIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        DetailIcon.Source = null;
        DetailIconFallback.Visibility = Visibility.Visible;
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
        => _ = Session.StartCommand.ExecuteAsync(null);
}

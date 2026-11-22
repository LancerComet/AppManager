using System.Collections.ObjectModel;
using System.ComponentModel;
using AppManager.Core.Models;
using AppManager.Core.Scanning;
using AppManager.Services;
using AppManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppManager.Controls;

/// <summary>
/// Shared master/detail "tool usage" view: a list of measured tools (AI tools
/// or developer tools) with a detail pane. Pages supply the session and the
/// page-specific localized strings via <see cref="Configure"/>.
/// </summary>
public sealed partial class ToolUsageView : UserControl, IDetachable
{
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(
        nameof(Session), typeof(HarnessSession), typeof(ToolUsageView),
        new PropertyMetadata(null));

    public HarnessSession Session
    {
        get => (HarnessSession)GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public ObservableCollection<HarnessItemViewModel> Items { get; } = new();

    /// <summary>Minimum size for entries shown in the detail list; totals and
    /// kind bars are unaffected. 1 shows every non-empty entry (AI page).</summary>
    public long MinEntryBytes { get; set; } = 1;

    private string? _pendingSelectKey;
    private PropertyChangedEventHandler? _sessionHandler;

    public ToolUsageView()
    {
        InitializeComponent();

        // Pin detail content to the viewport — see OverviewPage for why.
        DetailPane.SizeChanged += (_, e) =>
            DetailStack.Width = Math.Max(0, e.NewSize.Width - DetailPane.Padding.Right);
    }

    /// <summary>Wires the view to a session and resolves the page-specific
    /// strings. subtitleKey may take {0} = user name (like Harness_Subtitle);
    /// keys without placeholders ignore the argument.</summary>
    public void Configure(
        HarnessSession session, string titleKey, string subtitleKey,
        string emptyKey, string scanningKey)
    {
        Session = session;
        TitleText.Text = AppServices.Loc.Get(titleKey);
        SubtitleText.Text = AppServices.Loc.Format(subtitleKey, Environment.UserName);
        EmptyText.Text = AppServices.Loc.Get(emptyKey);
        ScanningText.Text = AppServices.Loc.Get(scanningKey);

        _sessionHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(HarnessSession.Result) or nameof(HarnessSession.IsScanning))
                Rebuild();
        };
        Session.PropertyChanged += _sessionHandler;
        Rebuild();
    }

    /// <summary>Page entry point: remembers an optional selection key and kicks
    /// off the lazy first scan.</summary>
    public void Activate(object? parameter)
    {
        if (parameter is string key)
            _pendingSelectKey = key;
        Session.EnsureScanned();
    }

    public void Detach()
    {
        if (_sessionHandler is not null)
            Session.PropertyChanged -= _sessionHandler;
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
        DetailName.Text = vm.Name;
        DetailPublisher.Text = vm.Subtitle;
        DetailTotal.Text = SizeFormatter.Format(u.TotalBytes);
        DetailExcluded.Text = u.ExcludedProgramBytes > 0
            ? AppServices.Loc.Format("Harness_ExcludedProgram", SizeFormatter.Format(u.ExcludedProgramBytes))
            : string.Empty;

        DetailIconView.Icon = null;
        DetailIconView.FallbackGlyph = vm.TypeGlyph;
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
            .Where(x => x.SizeBytes >= MinEntryBytes)
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
            DetailIconView.Icon = icon;
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
        => _ = Session.StartCommand.ExecuteAsync(null);
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using AppManager.Core.Models;
using AppManager.Services;
using AppManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppManager.Pages;

public sealed partial class UnattributedPage : Page, IDetachable
{
    public SettingsService Settings => AppServices.Settings;
    public ObservableCollection<UnattributedItemViewModel> Items { get; } = new();

    private readonly PropertyChangedEventHandler _sessionHandler;

    public UnattributedPage()
    {
        InitializeComponent();

        CategoryBox.Items.Add(new ComboBoxItem { Content = AppServices.Loc.Get("Filter_All"), Tag = "" });
        foreach (var c in CategoryNames.All)
            CategoryBox.Items.Add(new ComboBoxItem { Content = CategoryNames.Of(c), Tag = c.ToString() });

        _sessionHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(ScanSession.Result))
                Rebuild();
        };
        AppServices.Session.PropertyChanged += _sessionHandler;
        Rebuild();
    }

    public void Detach() => AppServices.Session.PropertyChanged -= _sessionHandler;

    private void Rebuild()
    {
        var result = AppServices.Session.Result;
        var search = SearchBox?.Text?.Trim() ?? string.Empty;
        var catTag = (CategoryBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

        Items.Clear();
        if (result is null)
            return;

        var folders = result.Unattributed.AsEnumerable();
        if (catTag.Length > 0 && Enum.TryParse<FolderCategory>(catTag, out var cat))
            folders = folders.Where(f => f.Category == cat);
        if (search.Length > 0)
            folders = folders.Where(f => f.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase));

        var list = folders.OrderByDescending(f => f.SizeBytes).ToList();
        var max = list.Count > 0 ? list[0].SizeBytes : 0;
        foreach (var f in list)
            Items.Add(UnattributedItemViewModel.Create(f, max));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Rebuild();
    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Rebuild();
}

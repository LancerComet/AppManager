using AppManager.Pages;
using AppManager.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppManager;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        NavView.Loaded += (_, _) =>
        {
            ApplySettingsLabel();
            if (NavFrame.Content is null)
                NavigateTo("overview");
        };
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppServices.MainWindow = this;

        ApplyTheme(AppServices.Settings.Theme);
        AppServices.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsService.Theme))
                ApplyTheme(AppServices.Settings.Theme);
        };
    }

    public void ApplyTheme(ThemeSetting theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                ThemeSetting.Light => ElementTheme.Light,
                ThemeSetting.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    private bool _suppressSelectionChanged;
    private int _settingsLabelAttempts;

    /// <summary>
    /// The built-in settings item is created when the NavigationView template is
    /// applied — which can lag Loaded on some launch paths, so retry a few ticks.
    /// </summary>
    private void ApplySettingsLabel()
    {
        if (NavView.SettingsItem is NavigationViewItem item)
        {
            item.Content = "设置";
            return;
        }
        if (_settingsLabelAttempts++ < 50)
            DispatcherQueue.TryEnqueue(ApplySettingsLabel);
    }

    /// <summary>Programmatic navigation (overview tiles / debug hooks).</summary>
    public void NavigateTo(string tag, object? parameter = null)
    {
        Type page;
        _suppressSelectionChanged = true;
        try
        {
            switch (tag)
            {
                case "overview":
                    SelectNavItem("overview");
                    page = typeof(OverviewPage);
                    break;
                case "apps":
                    SelectNavItem("apps");
                    page = typeof(AppsPage);
                    break;
                case "unattributed":
                    SelectNavItem("unattributed");
                    page = typeof(UnattributedPage);
                    break;
                case "settings":
                    NavView.SelectedItem = NavView.SettingsItem;
                    page = typeof(SettingsPage);
                    break;
                default:
                    return;
            }
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
        NavFrame.Navigate(page, parameter);
    }

    public void NavigateToApp(string groupKey) => NavigateTo("apps", groupKey);

    private void SelectNavItem(string tag)
    {
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tag)
            {
                NavView.SelectedItem = item;
                return;
            }
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

#if DEBUG
    /// <summary>DEBUG layout probe: sweep window widths, log element bounds to layout-log.txt.</summary>
    public async Task RunLayoutSweepAsync()
    {
        var sb = new System.Text.StringBuilder();
        var widths = new[] { 700, 900, 1008, 1100, 1200, 1300, 1400, 1500, 1568, 1700, 1900, 2100, 2200 };
        foreach (var tag in new[] { "overview", "settings" })
        {
            NavigateTo(tag);
            await Task.Delay(500);
            foreach (var paneOpen in new[] { true, false })
            {
                NavView.IsPaneOpen = paneOpen;
                await Task.Delay(300);
                foreach (var w in widths)
                {
                    AppWindow.Resize(new Windows.Graphics.SizeInt32(w, 1000));
                    await Task.Delay(350);
                    sb.AppendLine($"page={tag} " + SweepLine(paneOpen, w));
                }
            }
        }
        var path = Path.Combine(AppServices.Settings.StorageFolder, "layout-log.txt");
        File.WriteAllText(path, sb.ToString());
    }

    private string SweepLine(bool paneOpen, int targetW)
    {
        string R(FrameworkElement? el)
        {
            if (el is null)
                return "null";
            var p = el.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            return $"{p.X:F0}..{p.X + el.ActualWidth:F0}(w={el.ActualWidth:F0})";
        }
        var page = NavFrame.Content as Page;
        var scroller = page?.FindName("ContentScroller") as ScrollViewer;
        var panel = page?.FindName("ContentPanel") as FrameworkElement;
        return $"target={targetW} client={Content.XamlRoot.Size.Width:F0} paneOpen={paneOpen} " +
               $"mode={NavView.DisplayMode} frame=[{R(NavFrame)}] scroller=[{R(scroller)}] " +
               $"panel=[{R(panel)}] hoff={scroller?.HorizontalOffset:F0} " +
               $"extent={scroller?.ExtentWidth:F0} vp={scroller?.ViewportWidth:F0} " +
               $"scrollw={scroller?.ScrollableWidth:F0} desired={panel?.DesiredSize.Width:F0}";
    }
#endif

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressSelectionChanged)
            return;
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "overview":
                    NavFrame.Navigate(typeof(OverviewPage));
                    break;
                case "apps":
                    NavFrame.Navigate(typeof(AppsPage));
                    break;
                case "unattributed":
                    NavFrame.Navigate(typeof(UnattributedPage));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            }
        }
    }
}

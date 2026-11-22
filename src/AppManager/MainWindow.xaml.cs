using System.Runtime.InteropServices;
using AppManager.Pages;
using AppManager.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

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

        RestorePlacement();
        TrackPlacement();
    }

    // ---- Window placement persistence ----

    private RectInt32 _lastNormalRect;
    private bool _wasMaximized;

    private void RestorePlacement()
    {
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        var saved = AppServices.Settings.Window;
        if (saved is { Width: > 0, Height: > 0 })
        {
            var rect = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
            var display = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
            if (display is not null)
            {
                var wa = display.WorkArea;
                var w = Math.Min(saved.Width, wa.Width);
                var h = Math.Min(saved.Height, wa.Height);
                var x = Math.Clamp(saved.X, wa.X, wa.X + wa.Width - w);
                var y = Math.Clamp(saved.Y, wa.Y, wa.Y + wa.Height - h);
                _lastNormalRect = new RectInt32(x, y, w, h);
                AppWindow.MoveAndResize(_lastNormalRect);
                if (saved.IsMaximized)
                {
                    _wasMaximized = true;
                    presenter?.Maximize();
                }
                return;
            }
        }
        ApplyDefaultPlacement();
    }

    private void ApplyDefaultPlacement()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var wa = display.WorkArea;
        var w = Math.Min((int)Math.Round(1280 * scale), (int)Math.Round(wa.Width * 0.85));
        var h = Math.Min((int)Math.Round(800 * scale), (int)Math.Round(wa.Height * 0.85));
        _lastNormalRect = new RectInt32(
            wa.X + (wa.Width - w) / 2,
            wa.Y + (wa.Height - h) / 2,
            w, h);
        AppWindow.MoveAndResize(_lastNormalRect);
    }

    private void TrackPlacement()
    {
        AppWindow.Changed += (_, args) =>
        {
            var state = (AppWindow.Presenter as OverlappedPresenter)?.State;
            if (state == OverlappedPresenterState.Maximized)
                _wasMaximized = true;
            else if (state == OverlappedPresenterState.Restored)
                _wasMaximized = false;

            if ((args.DidSizeChange || args.DidPositionChange) &&
                state == OverlappedPresenterState.Restored)
            {
                _lastNormalRect = new RectInt32(
                    AppWindow.Position.X, AppWindow.Position.Y,
                    AppWindow.Size.Width, AppWindow.Size.Height);
            }
        };

        AppWindow.Closing += (_, _) =>
        {
            var state = (AppWindow.Presenter as OverlappedPresenter)?.State;
            var maximized = state == OverlappedPresenterState.Maximized ||
                            (state == OverlappedPresenterState.Minimized && _wasMaximized);
            AppServices.Settings.Window = new WindowPlacement
            {
                X = _lastNormalRect.X,
                Y = _lastNormalRect.Y,
                Width = _lastNormalRect.Width,
                Height = _lastNormalRect.Height,
                IsMaximized = maximized,
            };
        };
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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
                case "harnesses":
                    SelectNavItem("harnesses");
                    page = typeof(HarnessesPage);
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
                case "harnesses":
                    NavFrame.Navigate(typeof(HarnessesPage));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            }
        }
    }
}

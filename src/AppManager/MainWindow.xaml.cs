using System.Runtime.InteropServices;
using AppManager.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AppManager;

public sealed partial class MainWindow : Window
{
    private Shell _shell;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppServices.MainWindow = this;

        _shell = CreateShell("overview");
        Content = _shell;

        ApplyTheme(AppServices.Settings.Theme);
        AppServices.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsService.Theme))
                ApplyTheme(AppServices.Settings.Theme);
        };

        AppServices.Loc.LanguageChanged += OnLanguageChanged;

        RestorePlacement();
        TrackPlacement();
    }

    /// <summary>
    /// Rebuilds the whole shell so every {l:Loc} string and the root Language
    /// (CJK font selection) re-resolve in the new language. Scan state lives in
    /// AppServices singletons, so a running scan keeps going and the rebuilt
    /// pages re-bind to live progress.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        var tag = _shell.CurrentTag;
        _shell.Detach();
        _shell = CreateShell(tag);
        Content = _shell;
        ApplyTheme(AppServices.Settings.Theme);
    }

    private Shell CreateShell(string initialTag)
    {
        var shell = new Shell(initialTag)
        {
            Language = AppServices.Loc.EffectiveTag,
        };
        SetTitleBar(shell.TitleBarElement);
        return shell;
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

    /// <summary>Programmatic navigation (debug hooks / overview tiles).</summary>
    public void NavigateTo(string tag, object? parameter = null)
        => _shell.NavigateTo(tag, parameter);

    public void NavigateToApp(string groupKey) => _shell.NavigateToApp(groupKey);

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
                _shell.Nav.IsPaneOpen = paneOpen;
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
        var frame = _shell.Frame;
        var page = frame.Content as Page;
        var scroller = page?.FindName("ContentScroller") as ScrollViewer;
        var panel = page?.FindName("ContentPanel") as FrameworkElement;
        return $"target={targetW} client={Content.XamlRoot.Size.Width:F0} paneOpen={paneOpen} " +
               $"mode={_shell.Nav.DisplayMode} frame=[{R(frame)}] scroller=[{R(scroller)}] " +
               $"panel=[{R(panel)}] hoff={scroller?.HorizontalOffset:F0} " +
               $"extent={scroller?.ExtentWidth:F0} vp={scroller?.ViewportWidth:F0} " +
               $"scrollw={scroller?.ScrollableWidth:F0} desired={panel?.DesiredSize.Width:F0}";
    }
#endif
}

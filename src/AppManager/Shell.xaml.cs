using AppManager.Pages;
using AppManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AppManager;

/// <summary>
/// The window's shell: title bar + NavigationView + Frame. Rebuilt as a whole
/// on language change so every {l:Loc} value re-resolves in the new language.
/// </summary>
public sealed partial class Shell : UserControl
{
    private readonly string _initialTag;
    private string _currentTag = "overview";
    private readonly HashSet<object> _pages = new();
    private bool _suppressSelectionChanged;
    private int _settingsLabelAttempts;

    public Shell(string initialTag = "overview")
    {
        _initialTag = initialTag;
        InitializeComponent();

        NavFrame.Navigated += OnFrameNavigated;
        NavView.Loaded += (_, _) =>
        {
            ApplySettingsLabel();
            if (NavFrame.Content is null)
                NavigateTo(_initialTag);
        };
    }

    public TitleBar TitleBarElement => AppTitleBar;
    internal NavigationView Nav => NavView;
    internal Frame Frame => NavFrame;
    public string CurrentTag => _currentTag;

    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        // Track page instances so Detach() can unsubscribe singleton handlers
        // before the shell is discarded on a language switch.
        if (e.Content is not null)
            _pages.Add(e.Content);
    }

    /// <summary>Unsubscribes every cached page from singleton events.</summary>
    public void Detach()
    {
        foreach (var page in _pages)
        {
            if (page is IDetachable detachable)
                detachable.Detach();
        }
        _pages.Clear();
        NavFrame.Navigated -= OnFrameNavigated;
    }

    /// <summary>Programmatic navigation (language rebuild / debug hooks).</summary>
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
                case "devtools":
                    SelectNavItem("devtools");
                    page = typeof(DevToolsPage);
                    break;
                case "settings":
                    NavView.SelectedItem = NavView.SettingsItem;
                    page = typeof(SettingsPage);
                    break;
                default:
                    return;
            }
            _currentTag = tag;
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

    /// <summary>
    /// The built-in settings item is created when the NavigationView template is
    /// applied — which can lag Loaded on some launch paths, so retry a few ticks.
    /// </summary>
    private void ApplySettingsLabel()
    {
        if (NavView.SettingsItem is NavigationViewItem item)
        {
            item.Content = AppServices.Loc.Get("Nav_Settings");
            return;
        }
        if (_settingsLabelAttempts++ < 50)
            DispatcherQueue.TryEnqueue(ApplySettingsLabel);
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressSelectionChanged)
            return;
        if (args.IsSettingsSelected)
        {
            _currentTag = "settings";
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "overview":
                    _currentTag = "overview";
                    NavFrame.Navigate(typeof(OverviewPage));
                    break;
                case "apps":
                    _currentTag = "apps";
                    NavFrame.Navigate(typeof(AppsPage));
                    break;
                case "unattributed":
                    _currentTag = "unattributed";
                    NavFrame.Navigate(typeof(UnattributedPage));
                    break;
                case "harnesses":
                    _currentTag = "harnesses";
                    NavFrame.Navigate(typeof(HarnessesPage));
                    break;
                case "devtools":
                    _currentTag = "devtools";
                    NavFrame.Navigate(typeof(DevToolsPage));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            }
        }
    }
}

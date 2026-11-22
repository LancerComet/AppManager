using AppManager.Services;
using Microsoft.UI.Xaml;

namespace AppManager;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
#if DEBUG
        UnhandledException += OnUnhandledException;
#endif
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();

        _window.Activate();

        _ = AppServices.Session.LoadLastScanAsync();

#if DEBUG
        ApplyDebugHooks();
#endif
    }

#if DEBUG
    private void ApplyDebugHooks()
    {
        var startPage = Environment.GetEnvironmentVariable("APPMANAGER_START_PAGE");
        if (!string.IsNullOrEmpty(startPage) && _window is MainWindow main)
        {
            // The NavigationView's initial selection fires SelectionChanged during the
            // first layout pass (after OnLaunched) — defer so the debug page wins.
            main.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    string? param = null;
                    if (startPage.Equals("apps", StringComparison.OrdinalIgnoreCase))
                        param = Environment.GetEnvironmentVariable("APPMANAGER_SELECT_KEY")
                                ?? (Environment.GetEnvironmentVariable("APPMANAGER_AUTOSCAN") == "1"
                                    ? "auto-first"
                                    : null);
                    else if (startPage.Equals("harnesses", StringComparison.OrdinalIgnoreCase)
                             || startPage.Equals("devtools", StringComparison.OrdinalIgnoreCase))
                        param = Environment.GetEnvironmentVariable("APPMANAGER_SELECT_KEY");
                    main.NavigateTo(startPage.ToLowerInvariant(), param);
                });
        }

        if (Environment.GetEnvironmentVariable("APPMANAGER_LAYOUTSWEEP") == "1" &&
            _window is MainWindow sweepWin)
        {
            sweepWin.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                async () => await sweepWin.RunLayoutSweepAsync());
        }

        if (Environment.GetEnvironmentVariable("APPMANAGER_AUTOSCAN") == "1")
            _ = AppServices.Session.StartCommand.ExecuteAsync(null);
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppServices.Settings.StorageFolder, "unhandled-exception.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:O}] {e.Exception.GetType().FullName}: {e.Message}\n{e.Exception.StackTrace}\n---\n");
        }
        catch
        {
        }
    }
#endif
}

namespace AppManager.Services;

/// <summary>
/// Service locator — no DI container. All singletons live here.
/// </summary>
public static class AppServices
{
    static AppServices()
    {
        Settings = new SettingsService();
        Icons = new IconService();
        License = new StoreLicenseService();
        Loc = new LocalizationService();
        Loc.Initialize(Settings);
        Session = new ScanSession();
        Harness = new HarnessSession();
    }

    public static SettingsService Settings { get; }

    public static LocalizationService Loc { get; }

    public static ScanSession Session { get; }

    public static HarnessSession Harness { get; }

    public static ILicenseService License { get; }

    public static IconService Icons { get; }

    /// <summary>Set by MainWindow once the window exists.</summary>
    public static MainWindow? MainWindow { get; set; }
}

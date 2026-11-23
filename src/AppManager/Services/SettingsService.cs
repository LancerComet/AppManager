using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using AppManager.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppManager.Services;

/// <summary>
/// JSON-backed settings stored next to the app's local data.
/// </summary>
public partial class SettingsService : ObservableObject
{
    public string StorageFolder { get; }
    public string SettingsPath => Path.Combine(StorageFolder, "settings.json");
    public string LastScanPath => Path.Combine(StorageFolder, "last-scan.json");

    [ObservableProperty]
    public partial ThemeSetting Theme { get; set; }

    /// <summary>"system" or a BCP-47 tag (zh-Hans / zh-Hant / ja-JP / en-US).</summary>
    [ObservableProperty]
    public partial string Language { get; set; } = "system";

    [ObservableProperty]
    public partial bool HideLowConfidence { get; set; }

    public ObservableCollection<string> ExtraRoots { get; } = new();

    private WindowPlacement? _window;

    /// <summary>Saved window bounds; assigning persists immediately.</summary>
    public WindowPlacement? Window
    {
        get => _window;
        set
        {
            _window = value;
            Save();
        }
    }

    public SettingsService()
    {
        StorageFolder = ResolveStorageFolder();
        Directory.CreateDirectory(StorageFolder);
        Load();
        PropertyChanged += OnChanged;
        ExtraRoots.CollectionChanged += OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Save();

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Save();
    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e) => Save();

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;
            var persisted = JsonSerializer.Deserialize(
                File.ReadAllText(SettingsPath), SettingsJsonContext.Default.PersistedSettings);
            if (persisted is null)
                return;
            Theme = persisted.Theme;
            Language = persisted.Language ?? "system";
            HideLowConfidence = persisted.HideLowConfidence;
            _window = persisted.Window;
            foreach (var root in persisted.ExtraRoots)
                if (!string.IsNullOrWhiteSpace(root))
                    ExtraRoots.Add(root);
        }
        catch
        {
            // Corrupt settings file — start fresh.
        }
    }

    private void Save()
    {
        try
        {
            var persisted = new PersistedSettings
            {
                Theme = Theme,
                Language = Language,
                ExtraRoots = ExtraRoots.ToList(),
                HideLowConfidence = HideLowConfidence,
                Window = _window,
            };
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(persisted, SettingsJsonContext.Default.PersistedSettings));
        }
        catch
        {
        }
    }

    private static string ResolveStorageFolder()
    {
        try
        {
            var folder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            if (!string.IsNullOrEmpty(folder))
                return folder;
        }
        catch (InvalidOperationException)
        {
            // Unpackaged — no package-local ApplicationData.
        }
        catch
        {
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppManager");
    }
}

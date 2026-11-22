using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppManager.Services;

public enum ThemeSetting
{
    System,
    Light,
    Dark,
}

/// <summary>Saved main-window placement (physical pixels, normal/restored bounds).</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsMaximized { get; set; }
}

/// <summary>
/// JSON-backed settings stored next to the app's local data.
/// </summary>
public partial class SettingsService : ObservableObject
{
    private sealed class Persisted
    {
        public ThemeSetting Theme { get; set; }
        public string Language { get; set; } = "system";
        public List<string> ExtraRoots { get; set; } = new();
        public bool HideLowConfidence { get; set; }
        public WindowPlacement? Window { get; set; }
    }

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
            var persisted = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(SettingsPath));
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
            var persisted = new Persisted
            {
                Theme = Theme,
                Language = Language,
                ExtraRoots = ExtraRoots.ToList(),
                HideLowConfidence = HideLowConfidence,
                Window = _window,
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(persisted));
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

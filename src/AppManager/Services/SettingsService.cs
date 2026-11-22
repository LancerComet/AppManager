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

/// <summary>
/// JSON-backed settings stored next to the app's local data.
/// </summary>
public partial class SettingsService : ObservableObject
{
    private sealed class Persisted
    {
        public ThemeSetting Theme { get; set; }
        public List<string> ExtraRoots { get; set; } = new();
        public bool HideLowConfidence { get; set; }
    }

    public string StorageFolder { get; }
    public string SettingsPath => Path.Combine(StorageFolder, "settings.json");
    public string LastScanPath => Path.Combine(StorageFolder, "last-scan.json");

    [ObservableProperty]
    public partial ThemeSetting Theme { get; set; }

    [ObservableProperty]
    public partial bool HideLowConfidence { get; set; }

    public ObservableCollection<string> ExtraRoots { get; } = new();

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
            HideLowConfidence = persisted.HideLowConfidence;
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
                ExtraRoots = ExtraRoots.ToList(),
                HideLowConfidence = HideLowConfidence,
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

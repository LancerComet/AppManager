using System.Text.Json.Serialization;

namespace AppManager.Core.Settings;

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
/// DTO matching the on-disk settings.json exactly (PascalCase properties,
/// enum as number, compact output). Do not rename members — the file format
/// must stay backward compatible.
/// </summary>
public sealed class PersistedSettings
{
    public ThemeSetting Theme { get; set; }
    public string Language { get; set; } = "system";
    public List<string> ExtraRoots { get; set; } = new();
    public bool HideLowConfidence { get; set; }
    public WindowPlacement? Window { get; set; }
}

/// <summary>
/// Source-generated serializer for settings.json — required for NativeAOT
/// (reflection-based System.Text.Json is trimmed away).
/// </summary>
[JsonSerializable(typeof(PersistedSettings))]
public partial class SettingsJsonContext : JsonSerializerContext
{
}

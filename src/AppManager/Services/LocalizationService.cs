using System.Diagnostics;
using System.Globalization;
using AppManager.Core.Localization;
using Windows.ApplicationModel.Resources.Core;
using Windows.Globalization;

namespace AppManager.Services;

/// <summary>
/// Runtime UI localization. String lookups go through a ResourceContext whose
/// Language qualifier is pinned to the effective tag, so they bypass MRT's
/// cached default context and switch instantly without restart.
/// </summary>
public sealed class LocalizationService
{
    private readonly ResourceMap _map;
    private readonly ResourceContext _context = new();
    private ResourceContext? _fallbackContext;

    private string _effectiveTag = LanguageResolver.DefaultTag;
    private CultureInfo _culture = new(LanguageResolver.DefaultTag);

    public LocalizationService()
    {
        _map = ResourceManager.Current.MainResourceMap.GetSubtree("Resources");
    }

    /// <summary>The BCP-47 tag the UI is currently rendered in.</summary>
    public string EffectiveTag => _effectiveTag;

    /// <summary>Culture matching <see cref="EffectiveTag"/>, for dates/numbers.</summary>
    public CultureInfo Culture => _culture;

    public event EventHandler? LanguageChanged;

    private static IReadOnlyList<string> PreferredLanguages()
    {
        try
        {
            return Windows.System.UserProfile.GlobalizationPreferences.Languages;
        }
        catch
        {
            return new[] { CultureInfo.CurrentUICulture.Name };
        }
    }

    /// <summary>
    /// Resolves the persisted setting once at startup and watches
    /// <see cref="SettingsService.Language"/> for changes.
    /// </summary>
    public void Initialize(SettingsService settings)
    {
        ApplyEffective(LanguageResolver.Resolve(settings.Language, PreferredLanguages()));
        settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is SettingsService s && e.PropertyName == nameof(SettingsService.Language))
        {
            var tag = LanguageResolver.Resolve(s.Language, PreferredLanguages());
            if (tag != _effectiveTag)
            {
                ApplyEffective(tag);
                LanguageChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void ApplyEffective(string tag)
    {
        _effectiveTag = tag;
        _culture = new CultureInfo(tag);
        _context.QualifierValues["Language"] = tag;
        _fallbackContext = null;
        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = tag;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loc] PrimaryLanguageOverride failed: {ex.Message}");
        }
    }

    /// <summary>String for <paramref name="key"/> in the effective language.</summary>
    public string Get(string key)
        => TryGet(key) ?? TryGetIn(LanguageResolver.DefaultTag, key) ?? Miss(key);

    /// <summary>Formats <see cref="Get"/> using the effective culture.</summary>
    public string Format(string key, params object[] args)
        => string.Format(_culture, Get(key), args);

    /// <summary>Returns null when the key is absent — for optional resources.</summary>
    public string? TryGet(string key)
    {
        try
        {
            var candidate = _map.GetValue(key, _context);
            return candidate.ValueAsString;
        }
        catch
        {
            return null;
        }
    }

    private string? TryGetIn(string tag, string key)
    {
        try
        {
            _fallbackContext ??= new ResourceContext();
            _fallbackContext.QualifierValues["Language"] = tag;
            return _map.GetValue(key, _fallbackContext).ValueAsString;
        }
        catch
        {
            return null;
        }
    }

    private static string Miss(string key)
    {
        Debug.WriteLine($"[Loc] missing resource key: {key}");
        return key;
    }
}

using AppManager.Core.Imaging;
using AppManager.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AppManager.Controls;

/// <summary>
/// The single place app icons are rendered: a rounded tile with the icon at
/// ~72% of the tile, on a background chosen from the icon's tone and the
/// current theme (white-on-transparent icons get a dark tile in light theme,
/// and vice versa). Without an icon (or on decode failure) it shows the
/// accent fallback tile with a glyph or letter.
/// </summary>
public sealed partial class AppIconView : UserControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(LoadedIcon), typeof(AppIconView),
        new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty FallbackGlyphProperty = DependencyProperty.Register(
        nameof(FallbackGlyph), typeof(string), typeof(AppIconView),
        new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.Register(
        nameof(FallbackText), typeof(string), typeof(AppIconView),
        new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

    public static readonly DependencyProperty TileSizeProperty = DependencyProperty.Register(
        nameof(TileSize), typeof(double), typeof(AppIconView),
        new PropertyMetadata(36.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty TileCornerRadiusProperty = DependencyProperty.Register(
        nameof(TileCornerRadius), typeof(CornerRadius), typeof(AppIconView),
        new PropertyMetadata(new CornerRadius(8), OnVisualPropertyChanged));

    public LoadedIcon? Icon
    {
        get => (LoadedIcon?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Icon-font glyph shown when there is no icon (wins over FallbackText).</summary>
    public string FallbackGlyph
    {
        get => (string)GetValue(FallbackGlyphProperty);
        set => SetValue(FallbackGlyphProperty, value);
    }

    /// <summary>Text (typically a letter) shown when there is no icon and no glyph.</summary>
    public string FallbackText
    {
        get => (string)GetValue(FallbackTextProperty);
        set => SetValue(FallbackTextProperty, value);
    }

    public double TileSize
    {
        get => (double)GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    public CornerRadius TileCornerRadius
    {
        get => (CornerRadius)GetValue(TileCornerRadiusProperty);
        set => SetValue(TileCornerRadiusProperty, value);
    }

    private bool _imageFailed;

    public AppIconView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => UpdateVisual();
        UpdateVisual();
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (AppIconView)d;
        if (e.Property == IconProperty)
            view._imageFailed = false;
        view.UpdateVisual();
    }

    private void IconImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _imageFailed = true;
        IconImage.Source = null;
        UpdateVisual();
    }

    private void UpdateVisual()
    {
        Tile.Width = Tile.Height = TileSize;
        Tile.CornerRadius = TileCornerRadius;
        IconImage.Width = IconImage.Height = TileSize * 0.72;
        FallbackIcon.FontSize = TileSize * 0.42;
        FallbackLetter.FontSize = TileSize * 0.42;

        var icon = Icon;
        var hasIcon = icon is not null && !_imageFailed;
        IconImage.Source = hasIcon ? icon!.Source : null;
        IconImage.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;

        var showGlyph = !hasIcon && !string.IsNullOrEmpty(FallbackGlyph);
        var showLetter = !hasIcon && !showGlyph && !string.IsNullOrEmpty(FallbackText);
        FallbackIcon.Visibility = showGlyph ? Visibility.Visible : Visibility.Collapsed;
        FallbackLetter.Visibility = showLetter ? Visibility.Visible : Visibility.Collapsed;

        if (!hasIcon)
        {
            // The accent fallback tile every page used before.
            Tile.Background = Resource("AccentFillColorDefaultBrush");
            Tile.BorderBrush = new SolidColorBrush(Colors.Transparent);
            return;
        }

        var dark = ActualTheme == ElementTheme.Dark;
        if (icon!.Tone == IconTone.Light && !dark)
        {
            Tile.Background = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x2B, 0x2B, 0x2B));
            Tile.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x3A, 0x3A, 0x3A));
        }
        else if (icon.Tone == IconTone.Dark && dark)
        {
            Tile.Background = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));
            Tile.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xE0, 0xE0, 0xE0));
        }
        else
        {
            Tile.Background = Resource("ControlFillColorDefaultBrush");
            Tile.BorderBrush = Resource("CardStrokeColorDefaultBrush");
        }
    }

    private static Brush Resource(string key)
        => Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b
            ? b
            : new SolidColorBrush(Colors.Transparent);
}

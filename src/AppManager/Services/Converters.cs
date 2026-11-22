using AppManager.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace AppManager.Services;

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is not null && !(value is string s && s.Length == 0);
        if (parameter as string == "invert")
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is true;
        if (parameter as string == "invert")
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class NotBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is not true;
}

public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var result = value is not null && !(value is string s && s.Length == 0);
        if (parameter as string == "invert")
            result = !result;
        return result;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class DoubleToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => new GridLength(value is double d ? Math.Max(0, d) : 0, GridUnitType.Star);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class ConfidenceToBrushConverter : IValueConverter
{
    /// <summary>parameter "fg" → foreground brush; anything else → pill background.</summary>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var foreground = parameter as string == "fg";
        var key = value is MatchConfidence c
            ? (c switch
            {
                MatchConfidence.High => foreground ? "SystemFillColorSuccessBrush" : "SystemFillColorSuccessBackgroundBrush",
                MatchConfidence.Medium => foreground ? "SystemFillColorCautionBrush" : "SystemFillColorCautionBackgroundBrush",
                MatchConfidence.Low => foreground ? "SystemFillColorNeutralBrush" : "SystemFillColorNeutralBackgroundBrush",
                _ => foreground ? "SystemFillColorNeutralBrush" : "SystemFillColorNeutralBackgroundBrush",
            })
            : foreground ? "SystemFillColorNeutralBrush" : "SystemFillColorNeutralBackgroundBrush";
        if (Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush b)
            return b;
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Maps HarnessKind to a theme brush for the colored dot in per-kind rows.</summary>
public sealed class HarnessKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is HarnessKind k
            ? k switch
            {
                HarnessKind.Conversations => "AccentFillColorDefaultBrush",
                HarnessKind.Artifacts => "SystemFillColorSuccessBrush",
                HarnessKind.Snapshots => "SystemFillColorCautionBrush",
                HarnessKind.Cache => "SystemFillColorNeutralBrush",
                HarnessKind.Logs => "SystemFillColorAttentionBrush",
                HarnessKind.Extensions => "AccentFillColorSecondaryBrush",
                HarnessKind.OldVersions => "SystemFillColorCriticalBrush",
                _ => "SystemFillColorSolidNeutralBrush",
            }
            : "SystemFillColorSolidNeutralBrush";
        if (Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush b)
            return b;
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

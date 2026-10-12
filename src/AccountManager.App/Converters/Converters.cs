using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AccountManager.App.ViewModels;

namespace AccountManager.App.Converters;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            null => Visibility.Collapsed,
            string s => string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible,
            int i => i > 0 ? Visibility.Visible : Visibility.Collapsed,
            _ => Visibility.Visible,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            null => Visibility.Visible,
            string s => string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed,
            int i => i > 0 ? Visibility.Collapsed : Visibility.Visible,
            _ => Visibility.Collapsed,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps a status tone to the theme's semantic fill brush.</summary>
public sealed class ToneToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is StatusTone tone
            ? tone switch
            {
                StatusTone.Success => "SystemFillColorSuccessBrush",
                StatusTone.Caution => "SystemFillColorCautionBrush",
                StatusTone.Critical => "SystemFillColorCriticalBrush",
                StatusTone.Accent => "SystemAccentColorPrimaryBrush",
                _ => "TextFillColorTertiaryBrush",
            }
            : "TextFillColorTertiaryBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0–100 percentage to a progress value, clamped.</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? Math.Clamp(d, 0, 100) : 0d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

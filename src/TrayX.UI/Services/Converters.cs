using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TrayX.Contracts;

namespace TrayX.UI.Services;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert || string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasText = value is string text && !string.IsNullOrWhiteSpace(text);
        if (Invert)
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (Invert)
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class ResourceBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string;
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return Application.Current?.TryFindResource(key) as Brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class ShellIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value switch
        {
            string text => text,
            AppEntryDto app => app.TargetPath ?? app.ShortcutPath,
            TrayIconDto icon => icon.ExecutablePath,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.StartsWith("apps:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ShellIconProvider.Load(path);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

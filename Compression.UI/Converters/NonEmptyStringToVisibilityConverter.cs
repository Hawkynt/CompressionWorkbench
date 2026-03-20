using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Compression.UI.Converters;

/// <summary>
/// Converts a non-empty string to Visible, empty/null to Collapsed.
/// </summary>
internal sealed class NonEmptyStringToVisibilityConverter : IValueConverter {
  public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

  public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    => throw new NotSupportedException();
}

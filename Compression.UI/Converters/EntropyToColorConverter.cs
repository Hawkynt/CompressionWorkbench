using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace Compression.UI.Converters;

/// <summary>
/// Converts an entropy value (0-8) to a background color.
/// Blue=low entropy (structured), green=medium, yellow=compressed, red=high/random.
/// </summary>
internal sealed class EntropyToColorConverter : IValueConverter {
  public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
    if (value is not double entropy) return Brushes.Transparent;
    var color = EntropyToColor(entropy);
    // Use light tint for row backgrounds (30% opacity)
    if (parameter is string s && s == "row")
      return new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B));
    return new SolidColorBrush(color);
  }

  public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    => throw new NotSupportedException();

  internal static Color EntropyToColor(double entropy) {
    // 0-2: blue (plaintext/repeating)
    // 2-4: cyan-green (structured)
    // 4-6: green-yellow (light compression)
    // 6-7.5: orange (dictionary compression)
    // 7.5-8: red (strong compression/random/encrypted)
    var t = Math.Clamp(entropy / 8.0, 0, 1);
    byte r, g, b;
    if (t < 0.25) {
      // Blue to cyan
      var f = t / 0.25;
      r = (byte)(40 * f); g = (byte)(120 + 100 * f); b = (byte)(220 - 40 * f);
    }
    else if (t < 0.5) {
      // Cyan to green
      var f = (t - 0.25) / 0.25;
      r = (byte)(40 + 60 * f); g = (byte)(220 - 30 * f); b = (byte)(180 - 120 * f);
    }
    else if (t < 0.75) {
      // Green to yellow/orange
      var f = (t - 0.5) / 0.25;
      r = (byte)(100 + 140 * f); g = (byte)(190 - 40 * f); b = (byte)(60 - 30 * f);
    }
    else {
      // Orange to red
      var f = (t - 0.75) / 0.25;
      r = (byte)(240); g = (byte)(150 - 110 * f); b = (byte)(30 + 10 * f);
    }
    return Color.FromRgb(r, g, b);
  }
}
